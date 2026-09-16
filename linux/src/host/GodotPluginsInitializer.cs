using System.Runtime.InteropServices;
using Godot.Bridge;
using Godot.NativeInterop;

namespace GodotPlugins.Game;

// Only the SDK initializer generator is disabled. All script generators remain enabled.
internal static unsafe partial class Main
{
    [UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]
    private static godot_bool InitializeFromGameProject(IntPtr godotDllHandle,
        IntPtr outManagedCallbacks, IntPtr unmanagedCallbacks, int unmanagedCallbacksSize)
    {
        try
        {
            global::ManagedBootstrap.BeginEntry();
            DllImportResolver resolver = new GodotDllImportResolver(godotDllHandle).OnResolveDllImport;
            var coreApiAssembly = typeof(global::Godot.GodotObject).Assembly;
            NativeLibrary.SetDllImportResolver(coreApiAssembly, resolver);
            NativeFuncs.Initialize(unmanagedCallbacks, unmanagedCallbacksSize);

            // The SDK's complete table supplies every original pointer. No private-field reflection.
            ManagedCallbacks original = ManagedCallbacks.Create();
            ManagedCallbacks wrapped = global::ManagedBootstrap.InstallCallback(original);
            if (outManagedCallbacks == IntPtr.Zero)
                throw new InvalidDataException("Null native managed-callback output table");
            *(ManagedCallbacks*)outManagedCallbacks = wrapped;
            global::ManagedBootstrap.EntryTablePublished();

            // This means only that the callback table was filled. The 47 controls have NOT run yet.
            // Native GDMono publishes the global cache after this return, then invokes our wrapper.
            return godot_bool.True;
        }
        catch (Exception error)
        {
            global::ManagedBootstrap.Abort("initializer", error, 121);
            return godot_bool.False; // Unreachable: Abort always terminates the process.
        }
    }
}
