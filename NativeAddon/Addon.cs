#nullable enable

using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// A minimal Node.js native addon written in C# with Native AOT.
/// Exports functions callable from JavaScript via N-API.
/// </summary>
public static unsafe partial class Addon
{
    private enum Status { Ok = 0 }

    /// <summary>
    /// Entry point called by Node.js when the addon is loaded via require().
    /// Registers all exported functions on the module's exports object.
    /// </summary>
    [UnmanagedCallersOnly(
        EntryPoint = "napi_register_module_v1",
        CallConvs = [typeof(CallConvCdecl)])]
    public static nint Init(nint env, nint exports)
    {
        Initialize();

        RegisterFunction(env, exports, "greet"u8, &Greet);
        RegisterFunction(env, exports, "add"u8, &Add);

        return exports;
    }

    /// <summary>
    /// Sets up a DLL import resolver so that N-API functions (declared as
    /// [LibraryImport("node")] below) resolve against the host process
    /// (node.exe) rather than a separate shared library.
    /// </summary>
    private static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            Assembly.GetExecutingAssembly(),
            ResolveDllImport);

        static nint ResolveDllImport(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (libraryName is not "node")
                return 0;

            return NativeLibrary.GetMainProgramHandle();
        }
    }

    /// <summary>
    /// Registers a C# function pointer as a named JavaScript function
    /// on the given exports object.
    /// </summary>
    private static void RegisterFunction(
        nint env, nint exports, ReadOnlySpan<byte> name,
        delegate* unmanaged[Cdecl]<nint, nint, nint> callback)
    {
        fixed (byte* pName = name)
        {
            NApi.CreateFunction(env, pName, (nuint)name.Length, callback, 0, out nint fn);
            NApi.SetNamedProperty(env, exports, pName, fn);
        }
    }

    #region Exported functions

    /// <summary>
    /// greet(name: string): string — Returns a greeting message.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Greet(nint env, nint info)
    {
        try
        {
            var name = GetStringArg(env, info, 0);

            if (name is null)
            {
                ThrowError(env, "Expected a string argument: name");
                return 0;
            }

            return CreateJsString(env, $"Hello, {name}! Greetings from .NET Native AOT.");
        }
        catch (Exception ex)
        {
            ThrowError(env, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// add(a: number, b: number): number — Returns the sum of two numbers.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Add(nint env, nint info)
    {
        try
        {
            nuint argc = 2;
            nint* argv = stackalloc nint[2];
            NApi.GetCallbackInfo(env, info, &argc, argv, null, null);

            if (argc < 2)
            {
                ThrowError(env, "Expected two number arguments");
                return 0;
            }

            NApi.GetValueDouble(env, argv[0], out double a);
            NApi.GetValueDouble(env, argv[1], out double b);

            NApi.CreateDouble(env, a + b, out nint result);
            return result;
        }
        catch (Exception ex)
        {
            ThrowError(env, ex.Message);
            return 0;
        }
    }

    #endregion

    #region Marshalling helpers

    /// <summary>
    /// Reads a JavaScript string argument at the given index from a callback's arguments.
    /// Returns null if the argument is missing.
    /// </summary>
    private static string? GetStringArg(nint env, nint info, int index)
    {
        nuint argc = (nuint)(index + 1);
        nint* argv = stackalloc nint[index + 1];
        NApi.GetCallbackInfo(env, info, &argc, argv, null, null);

        if ((int)argc <= index)
            return null;

        // First call gets the UTF-8 byte length
        NApi.GetValueStringUtf8(env, argv[index], null, 0, out nuint len);

        // Allocate a buffer — on the stack for small strings, from the pool for large ones
        int bufLen = (int)len + 1;
        byte[]? rented = null;
        Span<byte> buf = bufLen <= 512
            ? stackalloc byte[bufLen]
            : (rented = ArrayPool<byte>.Shared.Rent(bufLen));

        try
        {
            fixed (byte* p = buf)
                NApi.GetValueStringUtf8(env, argv[index], p, len + 1, out _);

            return Encoding.UTF8.GetString(buf[..(int)len]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Creates a JavaScript string value from a .NET string.
    /// </summary>
    private static nint CreateJsString(nint env, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);

        byte[]? rented = null;
        Span<byte> buf = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            Encoding.UTF8.GetBytes(value, buf);
            nint result;
            fixed (byte* p = buf)
                NApi.CreateStringUtf8(env, p, (nuint)byteCount, out result);
            return result;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Throws a JavaScript Error with the given message.
    /// Important: unhandled exceptions in [UnmanagedCallersOnly] methods crash the
    /// host process, so we catch and forward them to JS via this helper.
    /// </summary>
    private static void ThrowError(nint env, string message)
    {
        int byteCount = Encoding.UTF8.GetByteCount(message);
        byte[]? rented = null;
        Span<byte> buf = byteCount < 512
            ? stackalloc byte[byteCount + 1]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount + 1));

        try
        {
            Encoding.UTF8.GetBytes(message, buf);
            buf[byteCount] = 0; // napi_throw_error requires a null-terminated string
            fixed (byte* p = buf)
                NApi.ThrowError(env, null, p);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    #endregion

    #region N-API P/Invoke declarations

    /// <summary>
    /// P/Invoke declarations for the N-API functions we use, resolved from
    /// the host process (node.exe) at runtime via the DLL import resolver.
    /// </summary>
    private static partial class NApi
    {
        [LibraryImport("node", EntryPoint = "napi_create_function")]
        internal static partial Status CreateFunction(
            nint env, byte* utf8name, nuint length,
            delegate* unmanaged[Cdecl]<nint, nint, nint> cb,
            nint data, out nint result);

        [LibraryImport("node", EntryPoint = "napi_set_named_property")]
        internal static partial Status SetNamedProperty(
            nint env, nint obj, byte* utf8name, nint value);

        [LibraryImport("node", EntryPoint = "napi_get_cb_info")]
        internal static partial Status GetCallbackInfo(
            nint env, nint cbinfo, nuint* argc,
            nint* argv, nint* thisArg, nint* data);

        [LibraryImport("node", EntryPoint = "napi_get_value_string_utf8")]
        internal static partial Status GetValueStringUtf8(
            nint env, nint value, byte* buf, nuint bufsize, out nuint result);

        [LibraryImport("node", EntryPoint = "napi_create_string_utf8")]
        internal static partial Status CreateStringUtf8(
            nint env, byte* str, nuint length, out nint result);

        [LibraryImport("node", EntryPoint = "napi_get_value_double")]
        internal static partial Status GetValueDouble(
            nint env, nint value, out double result);

        [LibraryImport("node", EntryPoint = "napi_create_double")]
        internal static partial Status CreateDouble(
            nint env, double value, out nint result);

        [LibraryImport("node", EntryPoint = "napi_throw_error")]
        internal static partial Status ThrowError(
            nint env, byte* code, byte* msg);
    }

    #endregion
}
