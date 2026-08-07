using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeGraph.Services;

internal sealed class AttachmentStorage(string configuredRoot) : IDisposable
{
    private readonly string lexicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
    private readonly object rootLock = new();
    private string? pinnedPhysicalRoot;
    private SafeFileHandle? pinnedUnixRootHandle;

    public async Task<(string Path, long Size)> CreateAsync(long pageId, Stream content)
    {
        return OperatingSystem.IsWindows()
            ? await CreatePortableAsync(pageId, content)
            : await CreateUnixAsync(pageId, content);
    }

    public Stream OpenRead(string storagePath)
    {
        return OperatingSystem.IsWindows()
            ? OpenReadPortable(storagePath)
            : OpenReadUnix(storagePath);
    }

    public AttachmentDeletionLease Quarantine(string storagePath)
    {
        return OperatingSystem.IsWindows()
            ? QuarantinePortable(storagePath)
            : QuarantineUnix(storagePath);
    }

    public void DeleteCreated(string storagePath)
    {
        using var lease = Quarantine(storagePath);
        lease.Commit();
    }

    private async Task<(string Path, long Size)> CreateUnixAsync(long pageId, Stream content)
    {
        using var root = OpenUnixRoot(create: true);
        var pageName = pageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var page = OpenOrCreateUnixDirectory(root.Handle, pageName);

        var storageName = Guid.NewGuid().ToString("N");
        using var fileHandle = OpenAt(
            page.Handle,
            storageName,
            UnixFlags.WriteOnly | UnixFlags.Create | UnixFlags.Exclusive | UnixFlags.NoFollow | UnixFlags.CloseOnExec,
            Convert.ToUInt32("600", 8));
        SetUnixMode(fileHandle, Convert.ToUInt32("600", 8));

        try
        {
            await using var destination = new FileStream(
                fileHandle,
                FileAccess.Write,
                bufferSize: 81920,
                isAsync: false);
            await content.CopyToAsync(destination);
            await destination.FlushAsync();
            return (Path.Combine(root.LexicalPath, pageName, storageName), destination.Length);
        }
        catch
        {
            UnlinkAt(page.Handle, storageName, ignoreMissing: true);
            throw;
        }
    }

    private Stream OpenReadUnix(string storagePath)
    {
        using var root = OpenUnixRoot(create: false);
        var parts = GetRelativeStorageParts(root.LexicalPath, storagePath);
        using var page = OpenAt(
            root.Handle,
            parts.Page,
            UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
        var file = OpenAt(
            page,
            parts.File,
            UnixFlags.ReadOnly | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
        return new FileStream(file, FileAccess.Read, bufferSize: 81920, isAsync: false);
    }

    private AttachmentDeletionLease QuarantineUnix(string storagePath)
    {
        var root = OpenUnixRoot(create: false);
        try
        {
            var parts = GetRelativeStorageParts(root.LexicalPath, storagePath);
            var page = OpenAt(
                root.Handle,
                parts.Page,
                UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
            root.Dispose();

            try
            {
                using (OpenAt(
                           page,
                           parts.File,
                           UnixFlags.ReadOnly | UnixFlags.NoFollow | UnixFlags.CloseOnExec))
                {
                }

                var quarantineName = $".delete-{Guid.NewGuid():N}";
                RenameAt(page, parts.File, page, quarantineName);
                return new UnixDeletionLease(page, parts.File, quarantineName);
            }
            catch (FileNotFoundException)
            {
                page.Dispose();
                return AttachmentDeletionLease.NoFile;
            }
            catch
            {
                page.Dispose();
                throw;
            }
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private UnixRoot OpenUnixRoot(bool create)
    {
        lock (rootLock)
        {
            if (pinnedPhysicalRoot is not null)
                return new UnixRoot(
                    Duplicate(pinnedUnixRootHandle!),
                    pinnedPhysicalRoot,
                    lexicalRoot);

            var (existingPath, missingParts) = FindExistingRootPrefix(create);
            var physicalExistingPath = RealPath(existingPath);
            var currentPath = physicalExistingPath;
            var current = OpenUnixAbsoluteDirectory(physicalExistingPath);

            try
            {
                foreach (var component in missingParts)
                {
                    SafeFileHandle next;
                    try
                    {
                        next = OpenAt(
                            current,
                            component,
                            UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
                    }
                    catch (FileNotFoundException) when (create)
                    {
                        MkdirAt(current, component);
                        next = OpenAt(
                            current,
                            component,
                            UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
                        SetUnixMode(next, Convert.ToUInt32("700", 8));
                    }

                    current.Dispose();
                    current = next;
                    currentPath = Path.Combine(currentPath, component);
                }

                pinnedPhysicalRoot = currentPath;
                pinnedUnixRootHandle = Duplicate(current);
                return new UnixRoot(current, currentPath, lexicalRoot);
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }
    }

    private (string ExistingPath, IReadOnlyList<string> MissingParts) FindExistingRootPrefix(bool create)
    {
        var missing = new Stack<string>();
        var existing = lexicalRoot;

        while (!Directory.Exists(existing))
        {
            if (!create)
                throw new DirectoryNotFoundException($"Attachment root '{lexicalRoot}' does not exist.");

            var name = Path.GetFileName(existing);
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent))
                throw new DirectoryNotFoundException($"Could not resolve attachment root '{lexicalRoot}'.");
            missing.Push(name);
            existing = parent;
        }

        return (existing, missing.ToArray());
    }

    private static SafeFileHandle OpenUnixAbsoluteDirectory(string path)
    {
        var components = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Open(
            Path.DirectorySeparatorChar.ToString(),
            UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
        try
        {
            foreach (var component in components)
            {
                var next = OpenAt(
                    current,
                    component,
                    UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static UnixRoot OpenOrCreateUnixDirectory(SafeFileHandle parent, string name)
    {
        try
        {
            return new UnixRoot(
                OpenAt(parent, name, UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec),
                name,
                name);
        }
        catch (FileNotFoundException)
        {
            MkdirAt(parent, name);
            var handle = OpenAt(
                parent,
                name,
                UnixFlags.ReadOnly | UnixFlags.Directory | UnixFlags.NoFollow | UnixFlags.CloseOnExec);
            SetUnixMode(handle, Convert.ToUInt32("700", 8));
            return new UnixRoot(handle, name, name);
        }
    }

    private async Task<(string Path, long Size)> CreatePortableAsync(long pageId, Stream content)
    {
        var root = ResolvePortableRoot(create: true);
        var pageName = pageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pageDirectory = Path.Combine(root, pageName);
        CreateAndValidatePortableDirectory(root, pageDirectory);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(pageDirectory, Guid.NewGuid().ToString("N"));
            FileStream destination;
            try
            {
                destination = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Delete,
                    81920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                continue;
            }

            await using (destination)
            {
                try
                {
                    ValidatePortableHandle(root, destination.SafeFileHandle);
                    SetWindowsDeleteDisposition(destination.SafeFileHandle, delete: false);
                    await content.CopyToAsync(destination);
                    await destination.FlushAsync();
                    return (Path.Combine(lexicalRoot, pageName, Path.GetFileName(path)), destination.Length);
                }
                catch
                {
                    SetWindowsDeleteDisposition(destination.SafeFileHandle, delete: true);
                    throw;
                }
            }
        }

        throw new IOException("Could not allocate a unique attachment storage path.");
    }

    private Stream OpenReadPortable(string storagePath)
    {
        var root = ResolvePortableRoot(create: false);
        var parts = GetRelativeStorageParts(lexicalRoot, storagePath);
        var physicalPath = Path.Combine(root, parts.Page, parts.File);
        var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ValidatePortableHandle(root, stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private AttachmentDeletionLease QuarantinePortable(string storagePath)
    {
        var root = ResolvePortableRoot(create: false);
        var parts = GetRelativeStorageParts(lexicalRoot, storagePath);
        var physicalPath = Path.Combine(root, parts.Page, parts.File);
        var handle = WindowsNative.CreateFile(
            physicalPath,
            WindowsNative.DeleteAccess | WindowsNative.FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            WindowsNative.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is WindowsNative.FileNotFound or WindowsNative.PathNotFound)
                return AttachmentDeletionLease.NoFile;
            throw new Win32Exception(error);
        }

        try
        {
            RejectWindowsReparsePoint(handle);
            ValidatePortableHandle(root, handle);
            return new WindowsHandleDeletionLease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private string ResolvePortableRoot(bool create)
    {
        lock (rootLock)
        {
            if (pinnedPhysicalRoot is not null)
                return pinnedPhysicalRoot;

            var (existing, missingParts) = FindExistingRootPrefix(create);
            var root = GetFinalPath(existing);
            foreach (var component in missingParts)
            {
                var next = Path.Combine(root, component);
                CreateAndValidatePortableDirectory(root, next);
                root = GetFinalPath(next);
            }

            pinnedPhysicalRoot = root;
            return root;
        }
    }

    private static void CreateAndValidatePortableDirectory(string root, string path)
    {
        Directory.CreateDirectory(path);
        var resolved = GetFinalPath(path);
        EnsureContained(root, resolved);
    }

    private static void ValidatePortableHandle(string root, SafeFileHandle handle)
    {
        EnsureContained(root, GetFinalPath(handle));
    }

    private static string GetFinalPath(string path)
    {
        using var handle = WindowsNative.CreateFile(
            path,
            WindowsNative.FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            WindowsNative.FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return GetFinalPath(handle);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = WindowsNative.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            return $"\\\\{path[8..]}";
        return path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    private static void SetWindowsDeleteDisposition(SafeFileHandle handle, bool delete)
    {
        var disposition = new WindowsNative.FileDispositionInfo { DeleteFile = delete };
        if (!WindowsNative.SetFileInformationByHandle(
                handle,
                WindowsNative.FileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<WindowsNative.FileDispositionInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void RejectWindowsReparsePoint(SafeFileHandle handle)
    {
        var information = new WindowsNative.FileAttributeTagInfo();
        if (!WindowsNative.GetFileInformationByHandleEx(
                handle,
                WindowsNative.FileInfoByHandleClass.FileAttributeTagInfo,
                ref information,
                (uint)Marshal.SizeOf<WindowsNative.FileAttributeTagInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if ((information.FileAttributes & WindowsNative.FileAttributeReparsePoint) != 0)
            throw new IOException("Attachment path resolves through a reparse point.");
    }

    private static (string Page, string File) GetRelativeStorageParts(string root, string storagePath)
    {
        var candidate = Path.GetFullPath(storagePath);
        EnsureContained(root, candidate);
        var parts = Path.GetRelativePath(root, candidate)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(p => p is "." or ".."))
            throw new IOException("Attachment storage path has an invalid shape.");
        return (parts[0], parts[1]);
    }

    private static void EnsureContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Attachment path escapes the configured storage root.");
        }
    }

    private static string RealPath(string path)
    {
        var pointer = UnixNative.RealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            throw CreateUnixException(path);
        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                   ?? throw new IOException($"Could not resolve attachment path '{path}'.");
        }
        finally
        {
            UnixNative.Free(pointer);
        }
    }

    private static SafeFileHandle Open(string path, int flags)
    {
        var fd = UnixNative.Open(path, flags, 0);
        return WrapFileDescriptor(fd, path);
    }

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags, uint mode = 0)
    {
        var fd = UnixNative.OpenAt(GetFileDescriptor(parent), name, flags, mode);
        return WrapFileDescriptor(fd, name);
    }

    private static SafeFileHandle WrapFileDescriptor(int fd, string path)
    {
        if (fd >= 0)
            return new SafeFileHandle((IntPtr)fd, ownsHandle: true);

        var error = Marshal.GetLastPInvokeError();
        var exception = CreateUnixException(path, error);
        if (error == UnixNative.NoEntry)
            throw new FileNotFoundException(exception.Message, path, exception);
        throw exception;
    }

    private static void MkdirAt(SafeFileHandle parent, string name)
    {
        if (UnixNative.MkdirAt(GetFileDescriptor(parent), name, Convert.ToUInt32("700", 8)) == 0)
            return;
        if (Marshal.GetLastPInvokeError() != UnixNative.Exists)
            throw CreateUnixException(name);
    }

    private static void RenameAt(
        SafeFileHandle oldParent,
        string oldName,
        SafeFileHandle newParent,
        string newName)
    {
        if (UnixNative.RenameAt(
                GetFileDescriptor(oldParent), oldName, GetFileDescriptor(newParent), newName) != 0)
            throw CreateUnixException(oldName);
    }

    private static void UnlinkAt(SafeFileHandle parent, string name, bool ignoreMissing)
    {
        if (UnixNative.UnlinkAt(GetFileDescriptor(parent), name, 0) == 0)
            return;
        if (!ignoreMissing || Marshal.GetLastPInvokeError() != UnixNative.NoEntry)
            throw CreateUnixException(name);
    }

    private static void SetUnixMode(SafeFileHandle handle, uint mode)
    {
        if (UnixNative.Fchmod(GetFileDescriptor(handle), mode) != 0)
            throw CreateUnixException("file descriptor");
    }

    private static int GetFileDescriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    private static SafeFileHandle Duplicate(SafeFileHandle handle)
    {
        var fd = UnixNative.Duplicate(GetFileDescriptor(handle));
        return WrapFileDescriptor(fd, "attachment root handle");
    }

    private static IOException CreateUnixException(string path, int? errorCode = null)
    {
        var error = errorCode ?? Marshal.GetLastPInvokeError();
        return new IOException($"Attachment filesystem operation failed for '{path}': {new Win32Exception(error).Message}");
    }

    public void Dispose()
    {
        lock (rootLock)
        {
            pinnedUnixRootHandle?.Dispose();
            pinnedUnixRootHandle = null;
        }
    }

    private sealed record UnixRoot(
        SafeFileHandle Handle,
        string PhysicalPath,
        string LexicalPath) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private sealed class UnixDeletionLease(
        SafeFileHandle parent,
        string originalName,
        string quarantineName) : AttachmentDeletionLease
    {
        protected override void CommitCore() => UnlinkAt(parent, quarantineName, ignoreMissing: false);

        protected override void RollbackCore() => RenameAt(parent, quarantineName, parent, originalName);

        protected override void DisposeCore() => parent.Dispose();
    }

    private sealed class WindowsHandleDeletionLease(SafeFileHandle handle) : AttachmentDeletionLease
    {
        protected override void CommitCore() => SetWindowsDeleteDisposition(handle, delete: true);

        protected override void RollbackCore()
        {
            // No filesystem mutation occurs until CommitCore. Keeping this exact validated
            // object open is the quarantine and makes parent-junction swaps irrelevant.
        }

        protected override void DisposeCore() => handle.Dispose();
    }

    private static class UnixFlags
    {
        public const int ReadOnly = 0;
        public const int WriteOnly = 1;
        public static int Create => OperatingSystem.IsMacOS() ? 0x0200 : 0x0040;
        public static int Exclusive => OperatingSystem.IsMacOS() ? 0x0800 : 0x0080;
        public static int NoFollow => OperatingSystem.IsMacOS() ? 0x0100 : 0x20000;
        public static int Directory => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        public static int CloseOnExec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    }

    private static partial class UnixNative
    {
        public const int NoEntry = 2;
        public const int Exists = 17;

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        public static extern int Open(string path, int flags, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
        public static extern int OpenAt(int directoryFd, string path, int flags, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "mkdirat")]
        public static extern int MkdirAt(int directoryFd, string path, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "renameat")]
        public static extern int RenameAt(int oldDirectoryFd, string oldPath, int newDirectoryFd, string newPath);

        [DllImport("libc", SetLastError = true, EntryPoint = "unlinkat")]
        public static extern int UnlinkAt(int directoryFd, string path, int flags);

        [DllImport("libc", SetLastError = true, EntryPoint = "fchmod")]
        public static extern int Fchmod(int fd, uint mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "dup")]
        public static extern int Duplicate(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
        public static extern IntPtr RealPath(string path, IntPtr resolvedPath);

        [DllImport("libc", EntryPoint = "free")]
        public static extern void Free(IntPtr pointer);
    }

    private static partial class WindowsNative
    {
        public const int FileNotFound = 2;
        public const int PathNotFound = 3;
        public const uint DeleteAccess = 0x00010000;
        public const uint FileReadAttributes = 0x80;
        public const uint FileFlagBackupSemantics = 0x02000000;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const uint FileAttributeReparsePoint = 0x00000400;

        public enum FileInfoByHandleClass
        {
            FileDispositionInfo = 4,
            FileAttributeTagInfo = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(
            string filename,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        public static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathSize,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileAttributeTagInfo fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileDispositionInfo fileInformation,
            uint bufferSize);
    }
}

internal abstract class AttachmentDeletionLease : IDisposable
{
    private bool completed;

    public static AttachmentDeletionLease NoFile { get; } = new NoFileDeletionLease();

    public void Commit()
    {
        CommitCore();
        completed = true;
    }

    public void Rollback()
    {
        completed = true;
        RollbackCore();
    }

    public void Dispose()
    {
        try
        {
            if (!completed)
                RollbackCore();
        }
        finally
        {
            DisposeCore();
        }
    }

    protected abstract void CommitCore();
    protected abstract void RollbackCore();
    protected virtual void DisposeCore()
    {
    }

    private sealed class NoFileDeletionLease : AttachmentDeletionLease
    {
        protected override void CommitCore()
        {
        }

        protected override void RollbackCore()
        {
        }
    }
}
