using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Ansight.Host.Files;

internal sealed record ArtifactApplication(string Id, string Name);

internal static class ArtifactPlatformApplications
{
    public static Task<IReadOnlyList<ArtifactApplication>> ListAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsApplicationsAsync(filePath, null, cancellationToken);
        }

        return Task.Run<IReadOnlyList<ArtifactApplication>>(() =>
        {
            if (OperatingSystem.IsMacOS())
            {
                return ReadMacApplications(filePath, cancellationToken);
            }

            if (OperatingSystem.IsLinux())
            {
                return ReadLinuxApplications(filePath, null, cancellationToken);
            }

            throw new PlatformNotSupportedException("Opening artifacts in applications is not supported on this operating system.");
        }, cancellationToken);
    }

    public static async Task OpenAsync(string filePath, string applicationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            throw new InvalidDataException("Choose an application to open the artifact.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            await ReadWindowsApplicationsAsync(filePath, applicationId, cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await Task.Run(() => ReadLinuxApplications(filePath, applicationId, cancellationToken), cancellationToken);
            return;
        }

        var applications = await ListAsync(filePath, cancellationToken);
        var application = applications.FirstOrDefault(candidate => string.Equals(candidate.Id, applicationId, StringComparison.Ordinal));
        if (application is null)
        {
            throw UnregisteredApplication();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add(application.Id);
        startInfo.ArgumentList.Add(Path.GetFullPath(filePath));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The operating system could not start the selected application.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), errorTask, outputTask).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "The operating system could not open the artifact in the selected application."
                    : error.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            StopLauncher(process);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The operating system did not respond when opening the artifact.");
            }
            throw;
        }
    }

    internal static void StopLauncher(Process process)
    {
        try
        {
            // Stop only the command helper; an application it already opened belongs to the user.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The helper may have exited between checking and terminating it.
        }
    }

    private static InvalidDataException UnregisteredApplication()
        => new("The selected application is no longer registered to open this artifact. Refresh the application list and try again.");

    [SupportedOSPlatform("windows")]
    public static Task RevealWindowsAsync(string filePath, CancellationToken cancellationToken)
        => RunWindowsAsync(() =>
        {
            Marshal.ThrowExceptionForHR(WindowsNative.SHParseDisplayName(Path.GetFullPath(filePath), IntPtr.Zero, out var item, 0, out _));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // With no child array, the shell opens this item's parent and selects the item.
                Marshal.ThrowExceptionForHR(WindowsNative.SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
                return [];
            }
            finally
            {
                Marshal.FreeCoTaskMem(item);
            }
        }, cancellationToken);

    private static IReadOnlyList<ArtifactApplication> ReadMacApplications(string filePath, CancellationToken cancellationToken)
    {
        var pathBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(filePath));
        var fileUrl = MacNative.CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathBytes, pathBytes.Length, false);
        if (fileUrl == IntPtr.Zero)
        {
            throw new InvalidOperationException("The operating system could not resolve the artifact file.");
        }

        try
        {
            // Request document viewers and editors, excluding executable-only handlers.
            var urls = MacNative.LSCopyApplicationURLsForURL(fileUrl, 0x00000002 | 0x00000004);
            if (urls == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                var applications = new List<ArtifactApplication>();
                var count = MacNative.CFArrayGetCount(urls);
                for (nint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url = MacNative.CFArrayGetValueAtIndex(urls, index);
                    var buffer = new byte[4096];
                    if (!MacNative.CFURLGetFileSystemRepresentation(url, true, buffer, buffer.Length))
                    {
                        continue;
                    }

                    var terminator = Array.IndexOf(buffer, (byte)0);
                    var applicationPath = Encoding.UTF8.GetString(buffer, 0, terminator < 0 ? buffer.Length : terminator);
                    if (Directory.Exists(applicationPath))
                    {
                        applications.Add(new ArtifactApplication(applicationPath, Path.GetFileNameWithoutExtension(applicationPath)));
                    }
                }

                return applications.DistinctBy(application => application.Id, StringComparer.Ordinal).ToArray();
            }
            finally
            {
                MacNative.CFRelease(urls);
            }
        }
        finally
        {
            MacNative.CFRelease(fileUrl);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<IReadOnlyList<ArtifactApplication>> ReadWindowsApplicationsAsync(
        string filePath,
        string? applicationId,
        CancellationToken cancellationToken)
        => RunWindowsAsync(() => ReadWindowsApplications(filePath, applicationId, cancellationToken), cancellationToken);

    [SupportedOSPlatform("windows")]
    private static Task<IReadOnlyList<ArtifactApplication>> RunWindowsAsync(
        Func<IReadOnlyList<ArtifactApplication>> action,
        CancellationToken cancellationToken)
    {
        // Shell association handlers are COM objects and must remain in one apartment.
        var completion = new TaskCompletionSource<IReadOnlyList<ArtifactApplication>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.ThrowExceptionForHR(WindowsNative.CoInitializeEx(IntPtr.Zero, 2));
                initialized = true;
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                if (initialized)
                {
                    WindowsNative.CoUninitialize();
                }
            }
        }) { IsBackground = true, Name = "Artifact application handlers" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ArtifactApplication> ReadWindowsApplications(
        string filePath,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            if (applicationId is not null)
            {
                throw UnregisteredApplication();
            }

            return [];
        }

        Marshal.ThrowExceptionForHR(WindowsNative.SHAssocEnumHandlers(extension, 0, out var enumerator));
        try
        {
            var applications = new List<ArtifactApplication>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = enumerator.Next(1, out var handler, out var fetched);
                Marshal.ThrowExceptionForHR(result);
                if (fetched == 0)
                {
                    break;
                }

                try
                {
                    if (handler.GetName(out var id) < 0 || string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    handler.GetUIName(out var name);
                    applications.Add(new ArtifactApplication(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(id) : name));
                    if (applicationId is not null && string.Equals(id, applicationId, StringComparison.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        InvokeWindowsHandler(filePath, handler);
                        return applications;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(handler);
                }
            }

            if (applicationId is not null)
            {
                throw UnregisteredApplication();
            }

            return applications.DistinctBy(application => application.Id, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void InvokeWindowsHandler(string filePath, WindowsNative.IAssocHandler handler)
    {
        var shellItemId = typeof(WindowsNative.IShellItem).GUID;
        Marshal.ThrowExceptionForHR(WindowsNative.SHCreateItemFromParsingName(Path.GetFullPath(filePath), IntPtr.Zero, ref shellItemId, out var item));
        try
        {
            var dataHandlerId = new Guid("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
            var dataObjectId = typeof(IDataObject).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref dataHandlerId, ref dataObjectId, out var data));
            try
            {
                Marshal.ThrowExceptionForHR(handler.Invoke(data));
            }
            finally
            {
                Marshal.ReleaseComObject(data);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private static IReadOnlyList<ArtifactApplication> ReadLinuxApplications(
        string filePath,
        string? applicationId,
        CancellationToken cancellationToken)
    {
        IntPtr file;
        try
        {
            file = LinuxNative.g_file_new_for_path(Path.GetFullPath(filePath));
        }
        catch (DllNotFoundException exception)
        {
            throw new PlatformNotSupportedException("Opening artifacts requires the desktop's GIO application service (libgio-2.0).", exception);
        }

        try
        {
            var info = LinuxNative.g_file_query_info(file, "standard::content-type", 0, IntPtr.Zero, out var error);
            if (info == IntPtr.Zero)
            {
                throw LinuxError(error, "The operating system could not determine the artifact's file type.");
            }

            IntPtr handlers;
            try
            {
                var contentType = LinuxNative.g_file_info_get_content_type(info);
                handlers = contentType == IntPtr.Zero ? IntPtr.Zero : LinuxNative.g_app_info_get_all_for_type(contentType);
            }
            finally
            {
                LinuxNative.g_object_unref(info);
            }

            try
            {
                var applications = new List<ArtifactApplication>();
                for (var node = handlers; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var handler = Marshal.ReadIntPtr(node);
                    var id = Marshal.PtrToStringUTF8(LinuxNative.g_app_info_get_id(handler));
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var name = Marshal.PtrToStringUTF8(LinuxNative.g_app_info_get_display_name(handler));
                    applications.Add(new ArtifactApplication(id, string.IsNullOrWhiteSpace(name) ? id : name));
                    if (applicationId is not null && string.Equals(id, applicationId, StringComparison.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var files = LinuxNative.g_list_append(IntPtr.Zero, file);
                        try
                        {
                            if (!LinuxNative.g_app_info_launch(handler, files, IntPtr.Zero, out error))
                            {
                                throw LinuxError(error, "The operating system could not open the artifact in the selected application.");
                            }
                        }
                        finally
                        {
                            LinuxNative.g_list_free(files);
                        }

                        return applications;
                    }
                }

                if (applicationId is not null)
                {
                    throw UnregisteredApplication();
                }

                return applications.DistinctBy(application => application.Id, StringComparer.Ordinal).ToArray();
            }
            finally
            {
                for (var node = handlers; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    LinuxNative.g_object_unref(Marshal.ReadIntPtr(node));
                }

                LinuxNative.g_list_free(handlers);
            }
        }
        finally
        {
            LinuxNative.g_object_unref(file);
        }
    }

    private static InvalidOperationException LinuxError(IntPtr error, string fallback)
    {
        if (error == IntPtr.Zero)
        {
            return new InvalidOperationException(fallback);
        }

        try
        {
            // GError contains a 32-bit domain, a 32-bit code, then a UTF-8 message pointer.
            var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallback : message);
        }
        finally
        {
            LinuxNative.g_error_free(error);
        }
    }

    private static class MacNative
    {
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";

        [DllImport(CoreFoundation)]
        public static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

        [DllImport(CoreServices)]
        public static extern IntPtr LSCopyApplicationURLsForURL(IntPtr url, uint roles);

        [DllImport(CoreFoundation)]
        public static extern nint CFArrayGetCount(IntPtr array);

        [DllImport(CoreFoundation)]
        public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CFURLGetFileSystemRepresentation(IntPtr url, [MarshalAs(UnmanagedType.I1)] bool resolveAgainstBase, [Out] byte[] buffer, nint capacity);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr value);
    }

    private static class WindowsNative
    {
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr reserved, uint apartment);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHParseDisplayName(string path, IntPtr bindContext, out IntPtr item, uint attributesIn, out uint attributesOut);

        [DllImport("shell32.dll", PreserveSig = true)]
        public static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers handlers);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItem item);

        [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IEnumAssocHandlers
        {
            [PreserveSig]
            int Next(uint count, out IAssocHandler handler, out uint fetched);
        }

        [ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAssocHandler
        {
            [PreserveSig]
            int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig]
            int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig]
            int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
            [PreserveSig]
            int IsRecommended();
            [PreserveSig]
            int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
            [PreserveSig]
            int Invoke([MarshalAs(UnmanagedType.Interface)] IDataObject data);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItem
        {
            [PreserveSig]
            int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IDataObject data);
        }
    }

    private static class LinuxNative
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string Glib = "libglib-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";

        [DllImport(Gio)]
        public static extern IntPtr g_file_new_for_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Gio)]
        public static extern IntPtr g_file_query_info(IntPtr file, [MarshalAs(UnmanagedType.LPUTF8Str)] string attributes, int flags, IntPtr cancellable, out IntPtr error);

        [DllImport(Gio)]
        public static extern IntPtr g_file_info_get_content_type(IntPtr info);

        [DllImport(Gio)]
        public static extern IntPtr g_app_info_get_all_for_type(IntPtr contentType);

        [DllImport(Gio)]
        public static extern IntPtr g_app_info_get_id(IntPtr application);

        [DllImport(Gio)]
        public static extern IntPtr g_app_info_get_display_name(IntPtr application);

        [DllImport(Gio)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool g_app_info_launch(IntPtr application, IntPtr files, IntPtr context, out IntPtr error);

        [DllImport(GObject)]
        public static extern void g_object_unref(IntPtr value);

        [DllImport(Glib)]
        public static extern IntPtr g_list_append(IntPtr list, IntPtr value);

        [DllImport(Glib)]
        public static extern void g_list_free(IntPtr list);

        [DllImport(Glib)]
        public static extern void g_error_free(IntPtr error);
    }
}
