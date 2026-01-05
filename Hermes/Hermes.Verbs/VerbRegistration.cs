using Hermes.Core;
using Hermes.Verbs.Filesystem;
using Hermes.Verbs.Help;
using Hermes.Verbs.Process;
using Hermes.Verbs.System;

namespace Hermes.Verbs;

/// <summary>
/// Registers all built-in Verbs with the executor.
/// </summary>
public static class VerbRegistration
{
    /// <summary>
    /// Registers all built-in Verbs with the given executor.
    /// </summary>
    /// <param name="executor">The executor to register Verbs with.</param>
    /// <param name="processOutputDirectory">Directory for process output files.</param>
    public static void RegisterAll(HermesVerbExecutor executor, string processOutputDirectory)
    {
        // Filesystem Verbs
        executor.Register<FsExistsArgs, FsExistsResult>("fs.exists", FilesystemHandlers.Exists);
        executor.Register<FsReadFileArgs, FsReadFileResult>("fs.readFile", FilesystemHandlers.ReadFile);
        executor.Register<FsReadRangeArgs, FsReadRangeResult>("fs.readRange", FilesystemHandlers.ReadRange);
        executor.Register<FsWriteFileArgs, FsWriteFileResult>("fs.writeFile", FilesystemHandlers.WriteFile);
        executor.Register<FsWriteRangeArgs, FsWriteRangeResult>("fs.writeRange", FilesystemHandlers.WriteRange);
        executor.Register<FsDeleteFileArgs, FsDeleteFileResult>("fs.deleteFile", FilesystemHandlers.DeleteFile);
        executor.Register<FsMoveFileArgs, FsMoveFileResult>("fs.moveFile", FilesystemHandlers.MoveFile);
        executor.Register<FsCopyFileArgs, FsCopyFileResult>("fs.copyFile", FilesystemHandlers.CopyFile);
        executor.Register<FsCreateDirectoryArgs, FsCreateDirectoryResult>("fs.createDirectory", FilesystemHandlers.CreateDirectory);
        executor.Register<FsDeleteDirectoryArgs, FsDeleteDirectoryResult>("fs.deleteDirectory", FilesystemHandlers.DeleteDirectory);
        executor.Register<FsLineCountArgs, FsLineCountResult>("fs.lineCount", FilesystemHandlers.LineCount);
        executor.Register<FsListDirArgs, FsListDirResult>("fs.listDir", FilesystemHandlers.ListDir);

        // Process Verbs
        executor.Register<ProcRunArgs, ProcRunResult>("proc.run", args => ProcessHandlers.Run(args, processOutputDirectory));

        // System Verbs
        executor.Register<SysMachineInfoArgs, SysMachineInfoResult>("sys.machineInfo", SystemHandlers.MachineInfo);

        // Help Verbs (capture executor for self-introspection)
        executor.Register<HelpArgs, HelpResult>("help", args => HelpHandlers.GetHelp(args, executor));
    }
}