using Gtk;
using LibHac;
using LibHac.Account;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.FsSystem;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using Ryujinx.UI.App.Common;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.UI.Common.Helper;
using Ryujinx.UI.Windows;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Ryujinx.UI.Widgets
{
    public partial class GameTableContextMenu : Menu
    {
        private readonly MainWindow _parent;
        private readonly VirtualFileSystem _virtualFileSystem;
        private readonly AccountManager _accountManager;
        private readonly HorizonClient _horizonClient;

        private readonly ApplicationData _applicationData;

        private MessageDialog _dialog;
        private bool _cancel;

        public GameTableContextMenu(MainWindow parent, VirtualFileSystem virtualFileSystem, AccountManager accountManager, HorizonClient horizonClient, ApplicationData applicationData)
        {
            _parent = parent;

            InitializeComponent();

            _virtualFileSystem = virtualFileSystem;
            _accountManager = accountManager;
            _horizonClient = horizonClient;
            _applicationData = applicationData;

            if (!_applicationData.ControlHolder.ByteSpan.IsZeros())
            {
                _openSaveUserDirMenuItem.Sensitive = _applicationData.ControlHolder.Value.UserAccountSaveDataSize > 0;
                _openSaveDeviceDirMenuItem.Sensitive = _applicationData.ControlHolder.Value.DeviceSaveDataSize > 0;
                _openSaveBcatDirMenuItem.Sensitive = _applicationData.ControlHolder.Value.BcatDeliveryCacheStorageSize > 0;
            }
            else
            {
                _openSaveUserDirMenuItem.Sensitive = false;
                _openSaveDeviceDirMenuItem.Sensitive = false;
                _openSaveBcatDirMenuItem.Sensitive = false;
            }

            string fileExt = System.IO.Path.GetExtension(_applicationData.Path).ToLower();
            bool hasNca = fileExt == ".nca" || fileExt == ".nsp" || fileExt == ".pfs0" || fileExt == ".xci";

            _extractRomFsMenuItem.Sensitive = hasNca;
            _extractExeFsMenuItem.Sensitive = hasNca;
            _extractLogoMenuItem.Sensitive = hasNca;

            _createShortcutMenuItem.Sensitive = !ReleaseInformation.IsFlatHubBuild;
            _trimXCIMenuItem.Sensitive = _applicationData != null && Ryujinx.Common.Utilities.XCIFileTrimmer.CanTrim(_applicationData.Path, new XCIFileTrimmerLog(_parent));

            PopupAtPointer(null);
        }

        private bool TryFindSaveData(string titleName, ulong titleId, BlitStruct<ApplicationControlProperty> controlHolder, in SaveDataFilter filter, out ulong saveDataId)
        {
            saveDataId = default;

            Result result = _horizonClient.Fs.FindSaveDataWithFilter(out SaveDataInfo saveDataInfo, SaveDataSpaceId.User, in filter);

            if (ResultFs.TargetNotFound.Includes(result))
            {
                ref ApplicationControlProperty control = ref controlHolder.Value;

                Logger.Info?.Print(LogClass.Application, $"Creating save directory for Title: {titleName} [{titleId:x16}]");

                if (Utilities.IsZeros(controlHolder.ByteSpan))
                {
                    // If the current application doesn't have a loaded control property, create a dummy one
                    // and set the savedata sizes so a user savedata will be created.
                    control = ref new BlitStruct<ApplicationControlProperty>(1).Value;

                    // The set sizes don't actually matter as long as they're non-zero because we use directory savedata.
                    control.UserAccountSaveDataSize = 0x4000;
                    control.UserAccountSaveDataJournalSize = 0x4000;

                    Logger.Warning?.Print(LogClass.Application, "No control file was found for this game. Using a dummy one instead. This may cause inaccuracies in some games.");
                }

                Uid user = new((ulong)_accountManager.LastOpenedUser.UserId.High, (ulong)_accountManager.LastOpenedUser.UserId.Low);

                result = _horizonClient.Fs.EnsureApplicationSaveData(out _, new LibHac.Ncm.ApplicationId(titleId), in control, in user);

                if (result.IsFailure())
                {
                    GtkDialog.CreateErrorDialog($"There was an error creating the specified savedata: {result.ToStringWithName()}");

                    return false;
                }

                // Try to find the savedata again after creating it
                result = _horizonClient.Fs.FindSaveDataWithFilter(out saveDataInfo, SaveDataSpaceId.User, in filter);
            }

            if (result.IsSuccess())
            {
                saveDataId = saveDataInfo.SaveDataId;

                return true;
            }

            GtkDialog.CreateErrorDialog($"There was an error finding the specified savedata: {result.ToStringWithName()}");

            return false;
        }

        private void OpenSaveDir(in SaveDataFilter saveDataFilter)
        {
            if (!TryFindSaveData(_applicationData.Name, _applicationData.Id, _applicationData.ControlHolder, in saveDataFilter, out ulong saveDataId))
            {
                return;
            }

            string saveRootPath = System.IO.Path.Combine(VirtualFileSystem.GetNandPath(), $"user/save/{saveDataId:x16}");

            if (!Directory.Exists(saveRootPath))
            {
                // Inconsistent state. Create the directory
                Directory.CreateDirectory(saveRootPath);
            }

            string committedPath = System.IO.Path.Combine(saveRootPath, "0");
            string workingPath = System.IO.Path.Combine(saveRootPath, "1");

            // If the committed directory exists, that path will be loaded the next time the savedata is mounted
            if (Directory.Exists(committedPath))
            {
                OpenHelper.OpenFolder(committedPath);
            }
            else
            {
                // If the working directory exists and the committed directory doesn't,
                // the working directory will be loaded the next time the savedata is mounted
                if (!Directory.Exists(workingPath))
                {
                    Directory.CreateDirectory(workingPath);
                }

                OpenHelper.OpenFolder(workingPath);
            }
        }

        private void ExtractSection(NcaSectionType ncaSectionType, int programIndex = 0)
        {
            FileChooserNative fileChooser = new("Choose the folder to extract into", _parent, FileChooserAction.SelectFolder, "Extract", "Cancel");

            ResponseType response = (ResponseType)fileChooser.Run();
            string destination = fileChooser.Filename;

            fileChooser.Dispose();

            if (response == ResponseType.Accept)
            {
                Thread extractorThread = new(() =>
                {
                    Gtk.Application.Invoke(delegate
                    {
                        _dialog = new MessageDialog(null, DialogFlags.DestroyWithParent, MessageType.Info, ButtonsType.Cancel, null)
                        {
                            Title = "Ryujinx - NCA Section Extractor",
                            Icon = new Gdk.Pixbuf(Assembly.GetAssembly(typeof(ConfigurationState)), "Ryujinx.Gtk3.UI.Common.Resources.Logo_Ryujinx.png"),
                            SecondaryText = $"Extracting {ncaSectionType} section from {System.IO.Path.GetFileName(_applicationData.Path)}...",
                            WindowPosition = WindowPosition.Center,
                        };

                        int dialogResponse = _dialog.Run();
                        if (dialogResponse == (int)ResponseType.Cancel || dialogResponse == (int)ResponseType.DeleteEvent)
                        {
                            _cancel = true;
                            _dialog.Dispose();
                        }
                    });

                    using FileStream file = new(_applicationData.Path, FileMode.Open, FileAccess.Read);

                    Nca mainNca = null;
                    Nca patchNca = null;

                    if ((System.IO.Path.GetExtension(_applicationData.Path).ToLower() == ".nsp") ||
                        (System.IO.Path.GetExtension(_applicationData.Path).ToLower() == ".pfs0") ||
                        (System.IO.Path.GetExtension(_applicationData.Path).ToLower() == ".xci"))
                    {
                        IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(_applicationData.Path, _virtualFileSystem);

                        foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                        {
                            using var ncaFile = new UniqueRef<IFile>();

                            pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                            Nca nca = new(_virtualFileSystem.KeySet, ncaFile.Release().AsStorage());

                            if (nca.Header.ContentType == NcaContentType.Program)
                            {
                                int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                if (nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                                {
                                    patchNca = nca;
                                }
                                else
                                {
                                    mainNca = nca;
                                }
                            }
                        }
                    }
                    else if (System.IO.Path.GetExtension(_applicationData.Path).ToLower() == ".nca")
                    {
                        mainNca = new Nca(_virtualFileSystem.KeySet, file.AsStorage());
                    }

                    if (mainNca == null)
                    {
                        Logger.Error?.Print(LogClass.Application, "Extraction failure. The main NCA is not present in the selected file.");

                        Gtk.Application.Invoke(delegate
                            {
                                GtkDialog.CreateErrorDialog("Extraction failure. The main NCA is not present in the selected file.");
                            });

                        return;
                    }

                    IntegrityCheckLevel checkLevel = ConfigurationState.Instance.System.EnableFsIntegrityChecks
                        ? IntegrityCheckLevel.ErrorOnInvalid
                        : IntegrityCheckLevel.None;

                    (Nca updatePatchNca, _) = mainNca.GetUpdateData(_virtualFileSystem, checkLevel, programIndex, out _);

                    if (updatePatchNca != null)
                    {
                        patchNca = updatePatchNca;
                    }

                    int index = Nca.GetSectionIndexFromType(ncaSectionType, mainNca.Header.ContentType);

                    bool sectionExistsInPatch = false;

                    if (patchNca != null)
                    {
                        sectionExistsInPatch = patchNca.CanOpenSection(index);
                    }

                    IFileSystem ncaFileSystem = sectionExistsInPatch ? mainNca.OpenFileSystemWithPatch(patchNca, index, IntegrityCheckLevel.ErrorOnInvalid)
                                                                         : mainNca.OpenFileSystem(index, IntegrityCheckLevel.ErrorOnInvalid);

                    FileSystemClient fsClient = _horizonClient.Fs;

                    string source = DateTime.Now.ToFileTime().ToString()[10..];
                    string output = DateTime.Now.ToFileTime().ToString()[10..];

                    using var uniqueSourceFs = new UniqueRef<IFileSystem>(ncaFileSystem);
                    using var uniqueOutputFs = new UniqueRef<IFileSystem>(new LocalFileSystem(destination));

                    fsClient.Register(source.ToU8Span(), ref uniqueSourceFs.Ref);
                    fsClient.Register(output.ToU8Span(), ref uniqueOutputFs.Ref);

                    (Result? resultCode, bool canceled) = CopyDirectory(fsClient, $"{source}:/", $"{output}:/");

                    if (!canceled)
                    {
                        if (resultCode.Value.IsFailure())
                        {
                            Logger.Error?.Print(LogClass.Application, $"LibHac returned error code: {resultCode.Value.ErrorCode}");

                            Gtk.Application.Invoke(delegate
                                {
                                    _dialog?.Dispose();

                                    GtkDialog.CreateErrorDialog("Extraction failed. Read the log file for further information.");
                                });
                        }
                        else if (resultCode.Value.IsSuccess())
                        {
                            Gtk.Application.Invoke(delegate
                                {
                                    _dialog?.Dispose();

                                    MessageDialog dialog = new(null, DialogFlags.DestroyWithParent, MessageType.Info, ButtonsType.Ok, null)
                                    {
                                        Title = "Ryujinx - NCA Section Extractor",
                                        Icon = new Gdk.Pixbuf(Assembly.GetAssembly(typeof(ConfigurationState)), "Ryujinx.UI.Common.Resources.Logo_Ryujinx.png"),
                                        SecondaryText = "Extraction completed successfully.",
                                        WindowPosition = WindowPosition.Center,
                                    };

                                    dialog.Run();
                                    dialog.Dispose();
                                });
                        }
                    }

                    fsClient.Unmount(source.ToU8Span());
                    fsClient.Unmount(output.ToU8Span());
                })
                {
                    Name = "GUI.NcaSectionExtractorThread",
                    IsBackground = true,
                };
                extractorThread.Start();
            }
        }

        private (Result? result, bool canceled) CopyDirectory(FileSystemClient fs, string sourcePath, string destPath)
        {
            Result rc = fs.OpenDirectory(out DirectoryHandle sourceHandle, sourcePath.ToU8Span(), OpenDirectoryMode.All);
            if (rc.IsFailure())
            {
                return (rc, false);
            }

            using (sourceHandle)
            {
                foreach (DirectoryEntryEx entry in fs.EnumerateEntries(sourcePath, "*", SearchOptions.Default))
                {
                    if (_cancel)
                    {
                        return (null, true);
                    }

                    string subSrcPath = PathTools.Normalize(PathTools.Combine(sourcePath, entry.Name));
                    string subDstPath = PathTools.Normalize(PathTools.Combine(destPath, entry.Name));

                    if (entry.Type == DirectoryEntryType.Directory)
                    {
                        fs.EnsureDirectoryExists(subDstPath);

                        (Result? result, bool canceled) = CopyDirectory(fs, subSrcPath, subDstPath);
                        if (canceled || result.Value.IsFailure())
                        {
                            return (result, canceled);
                        }
                    }

                    if (entry.Type == DirectoryEntryType.File)
                    {
                        fs.CreateOrOverwriteFile(subDstPath, entry.Size);

                        rc = CopyFile(fs, subSrcPath, subDstPath);
                        if (rc.IsFailure())
                        {
                            return (rc, false);
                        }
                    }
                }
            }

            return (Result.Success, false);
        }

        public static Result CopyFile(FileSystemClient fs, string sourcePath, string destPath)
        {
            Result rc = fs.OpenFile(out FileHandle sourceHandle, sourcePath.ToU8Span(), OpenMode.Read);
            if (rc.IsFailure())
            {
                return rc;
            }

            using (sourceHandle)
            {
                rc = fs.OpenFile(out FileHandle destHandle, destPath.ToU8Span(), OpenMode.Write | OpenMode.AllowAppend);
                if (rc.IsFailure())
                {
                    return rc;
                }

                using (destHandle)
                {
                    const int MaxBufferSize = 1024 * 1024;

                    rc = fs.GetFileSize(out long fileSize, sourceHandle);
                    if (rc.IsFailure())
                    {
                        return rc;
                    }

                    int bufferSize = (int)Math.Min(MaxBufferSize, fileSize);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                    try
                    {
                        for (long offset = 0; offset < fileSize; offset += bufferSize)
                        {
                            int toRead = (int)Math.Min(fileSize - offset, bufferSize);
                            Span<byte> buf = buffer.AsSpan(0, toRead);

                            rc = fs.ReadFile(out long _, sourceHandle, offset, buf);
                            if (rc.IsFailure())
                            {
                                return rc;
                            }

                            rc = fs.WriteFile(destHandle, offset, buf, WriteOption.None);
                            if (rc.IsFailure())
                            {
                                return rc;
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    rc = fs.FlushFile(destHandle);
                    if (rc.IsFailure())
                    {
                        return rc;
                    }
                }
            }

            return Result.Success;
        }

        //
        // Events
        //
        private void OpenSaveUserDir_Clicked(object sender, EventArgs args)
        {
            var userId = new LibHac.Fs.UserId((ulong)_accountManager.LastOpenedUser.UserId.High, (ulong)_accountManager.LastOpenedUser.UserId.Low);
            var saveDataFilter = SaveDataFilter.Make(_applicationData.Id, saveType: default, userId, saveDataId: default, index: default);

            OpenSaveDir(in saveDataFilter);
        }

        private void OpenSaveDeviceDir_Clicked(object sender, EventArgs args)
        {
            var saveDataFilter = SaveDataFilter.Make(_applicationData.Id, SaveDataType.Device, userId: default, saveDataId: default, index: default);

            OpenSaveDir(in saveDataFilter);
        }

        private void OpenSaveBcatDir_Clicked(object sender, EventArgs args)
        {
            var saveDataFilter = SaveDataFilter.Make(_applicationData.Id, SaveDataType.Bcat, userId: default, saveDataId: default, index: default);

            OpenSaveDir(in saveDataFilter);
        }

        private void ManageTitleUpdates_Clicked(object sender, EventArgs args)
        {
            new TitleUpdateWindow(_parent, _virtualFileSystem, _applicationData).Show();
        }

        private void ManageDlc_Clicked(object sender, EventArgs args)
        {
            new DlcWindow(_virtualFileSystem, _applicationData.IdBaseString, _applicationData).Show();
        }

        private void ManageCheats_Clicked(object sender, EventArgs args)
        {
            new CheatWindow(_virtualFileSystem, _applicationData.Id, _applicationData.Name, _applicationData.Path).Show();
        }

        private void OpenTitleModDir_Clicked(object sender, EventArgs args)
        {
            string modsBasePath = ModLoader.GetModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(modsBasePath, _applicationData.IdString);

            OpenHelper.OpenFolder(titleModsPath);
        }

        private void OpenTitleSdModDir_Clicked(object sender, EventArgs args)
        {
            string sdModsBasePath = ModLoader.GetSdModsBasePath();
            string titleModsPath = ModLoader.GetApplicationDir(sdModsBasePath, _applicationData.IdString);

            OpenHelper.OpenFolder(titleModsPath);
        }

        private void ExtractRomFs_Clicked(object sender, EventArgs args)
        {
            ExtractSection(NcaSectionType.Data);
        }

        private void ExtractExeFs_Clicked(object sender, EventArgs args)
        {
            ExtractSection(NcaSectionType.Code);
        }

        private void ExtractLogo_Clicked(object sender, EventArgs args)
        {
            ExtractSection(NcaSectionType.Logo);
        }

        private void OpenPtcDir_Clicked(object sender, EventArgs args)
        {
            string ptcDir = System.IO.Path.Combine(AppDataManager.GamesDirPath, _applicationData.IdString, "cache", "cpu");

            string mainPath = System.IO.Path.Combine(ptcDir, "0");
            string backupPath = System.IO.Path.Combine(ptcDir, "1");

            if (!Directory.Exists(ptcDir))
            {
                Directory.CreateDirectory(ptcDir);
                Directory.CreateDirectory(mainPath);
                Directory.CreateDirectory(backupPath);
            }

            OpenHelper.OpenFolder(ptcDir);
        }

        private void OpenShaderCacheDir_Clicked(object sender, EventArgs args)
        {
            string shaderCacheDir = System.IO.Path.Combine(AppDataManager.GamesDirPath, _applicationData.IdString, "cache", "shader");

            if (!Directory.Exists(shaderCacheDir))
            {
                Directory.CreateDirectory(shaderCacheDir);
            }

            OpenHelper.OpenFolder(shaderCacheDir);
        }

        private void PurgePtcCache_Clicked(object sender, EventArgs args)
        {
            DirectoryInfo mainDir = new(System.IO.Path.Combine(AppDataManager.GamesDirPath, _applicationData.IdString, "cache", "cpu", "0"));
            DirectoryInfo backupDir = new(System.IO.Path.Combine(AppDataManager.GamesDirPath, _applicationData.IdString, "cache", "cpu", "1"));

            MessageDialog warningDialog = GtkDialog.CreateConfirmationDialog("Warning", $"You are about to queue a PPTC rebuild on the next boot of:\n\n<b>{_applicationData.Name}</b>\n\nAre you sure you want to proceed?");

            List<FileInfo> cacheFiles = new();

            if (mainDir.Exists)
            {
                cacheFiles.AddRange(mainDir.EnumerateFiles("*.cache"));
            }

            if (backupDir.Exists)
            {
                cacheFiles.AddRange(backupDir.EnumerateFiles("*.cache"));
            }

            if (cacheFiles.Count > 0 && warningDialog.Run() == (int)ResponseType.Yes)
            {
                foreach (FileInfo file in cacheFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception e)
                    {
                        GtkDialog.CreateErrorDialog($"Error purging PPTC cache {file.Name}: {e}");
                    }
                }
            }

            warningDialog.Dispose();
        }

        private void PurgeShaderCache_Clicked(object sender, EventArgs args)
        {
            DirectoryInfo shaderCacheDir = new(System.IO.Path.Combine(AppDataManager.GamesDirPath, _applicationData.IdString, "cache", "shader"));

            using MessageDialog warningDialog = GtkDialog.CreateConfirmationDialog("Warning", $"You are about to delete the shader cache for :\n\n<b>{_applicationData.Name}</b>\n\nAre you sure you want to proceed?");

            List<DirectoryInfo> oldCacheDirectories = new();
            List<FileInfo> newCacheFiles = new();

            if (shaderCacheDir.Exists)
            {
                oldCacheDirectories.AddRange(shaderCacheDir.EnumerateDirectories("*"));
                newCacheFiles.AddRange(shaderCacheDir.GetFiles("*.toc"));
                newCacheFiles.AddRange(shaderCacheDir.GetFiles("*.data"));
            }

            if ((oldCacheDirectories.Count > 0 || newCacheFiles.Count > 0) && warningDialog.Run() == (int)ResponseType.Yes)
            {
                foreach (DirectoryInfo directory in oldCacheDirectories)
                {
                    try
                    {
                        directory.Delete(true);
                    }
                    catch (Exception e)
                    {
                        GtkDialog.CreateErrorDialog($"Error purging shader cache at {directory.Name}: {e}");
                    }
                }

                foreach (FileInfo file in newCacheFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception e)
                    {
                        GtkDialog.CreateErrorDialog($"Error purging shader cache at {file.Name}: {e}");
                    }
                }
            }
        }

        private void CreateShortcut_Clicked(object sender, EventArgs args)
        {
            IntegrityCheckLevel checkLevel = ConfigurationState.Instance.System.EnableFsIntegrityChecks
                ? IntegrityCheckLevel.ErrorOnInvalid
                : IntegrityCheckLevel.None;
            byte[] appIcon = new ApplicationLibrary(_virtualFileSystem, checkLevel).GetApplicationIcon(_applicationData.Path, ConfigurationState.Instance.System.Language, _applicationData.Id);
            ShortcutHelper.CreateAppShortcut(_applicationData.Path, _applicationData.Name, _applicationData.IdString, appIcon);
        }

        private void ProcessTrimResult(String filename, Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome operationOutcome)
        {
            string notifyUser = null;

            switch (operationOutcome)
            {
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.NoTrimNecessary:
                    notifyUser = "XCI File does not need to be trimmed. Check logs for further details";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.ReadOnlyFileCannotFix:
                    notifyUser = "XCI File is Read Only and could not be made writable. Check logs for further details";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.FreeSpaceCheckFailed:
                    notifyUser = "XCI File has data in the free space area, it is not safe to trim";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.InvalidXCIFile:
                    notifyUser = "XCI File contains invalid data. Check logs for further details";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.FileIOWriteError:
                    notifyUser = "XCI File could not be opened for writing. Check logs for further details";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.FileSizeChanged:
                    notifyUser = "XCI File has changed in size since it was scanned. Please check the file is not being written to and try again.";
                    break;
                case Ryujinx.Common.Utilities.XCIFileTrimmer.OperationOutcome.Successful:
                    _parent.UpdateGameTable();
                    break;
            }

            if (notifyUser != null)
            {
                GtkDialog.CreateWarningDialog("Trimming of the XCI file failed", notifyUser);
            }
        }

        private void TrimXCI_Clicked(object sender, EventArgs args)
        {
            if (_applicationData?.Path == null)
            {
                return;
            }

            var trimmer = new XCIFileTrimmer(_applicationData.Path, new XCIFileTrimmerLog(_parent));

            if (trimmer.CanBeTrimmed)
            {
                var savings = (double)trimmer.DiskSpaceSavingsB / 1024.0 / 1024.0;
                var currentFileSize = (double)trimmer.FileSizeB / 1024.0 / 1024.0;
                var cartDataSize = (double)trimmer.DataSizeB / 1024.0 / 1024.0;

                using MessageDialog confirmationDialog = GtkDialog.CreateConfirmationDialog(
                    $"This function will first check the empty space and then trim the XCI File to save disk space. Continue?",
                    $"Current File Size: {currentFileSize:n} MB\n" +
                    $"Game Data Size: {cartDataSize:n} MB\n" +
                    $"Disk Space Savings: {savings:n} MB\n"
                );

                if (confirmationDialog.Run() == (int)ResponseType.Yes)
                {
                    Thread xciFileTrimmerThread = new(() =>
                    {
                        _parent.StartProgress($"Trimming file '{_applicationData.Path}");

                        try
                        {
                            XCIFileTrimmer.OperationOutcome operationOutcome = trimmer.Trim();

                            Gtk.Application.Invoke(delegate
                            {
                                ProcessTrimResult(_applicationData.Path, operationOutcome);
                            });
                        }
                        finally
                        {
                            _parent.EndProgress();
                        }
                    })
                    {
                        Name = "GUI.XCIFileTrimmerThread",
                        IsBackground = true,
                    };
                    xciFileTrimmerThread.Start();
                }
            }
        }
    }
}
