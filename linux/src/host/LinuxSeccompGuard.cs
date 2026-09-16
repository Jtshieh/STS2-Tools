using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace E003.Linux;

/// <summary>
/// One Linux x86_64 filter for the E003 no-game host. It is not a portable sandbox.
/// Call before loading any game assembly/resource. A nonzero TSYNC return is failure.
/// </summary>
public static class LinuxSeccompGuard
{
    public const uint AuditArchX64 = 0xC000003E;
    public const uint Allow = 0x7FFF0000;
    public const uint KillProcess = 0x80000000;
    public const uint Denied = 0x00050001; // SECCOMP_RET_ERRNO | EPERM
    public const uint NotImplemented = 0x00050026; // ENOSYS: libc must fall back from clone3.
    public const uint CloneThread = 0x00010000;
    private const uint CloneNewNamespaces = 0x7E020080;

    // x86_64 syscall numbers only. No socket creation/use, new process/exec, mount,
    // namespace change, cross-process injection or alternate io_uring entry point.
    public static readonly int[] DeniedSyscalls =
    [41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55,
     57, 58, 59, 101, 155, 161, 165, 166, 272, 288, 298, 299, 304,
     307, 308, 310, 311, 321, 322, 425, 426, 427];

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly record struct Instruction(ushort Code, byte JumpTrue, byte JumpFalse, uint Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterProgram { public ushort Length; public IntPtr Instructions; }

    public sealed record SyscallResult(long ReturnValue, int Errno);
    public sealed record FilterCase(string Name, uint Actual, uint Expected, bool Passed);
    public sealed record Installation(bool Passed, string Architecture, string FilterSha256,
        Instruction[] Filter, FilterCase[] Cases, SyscallResult? NoNewPrivileges,
        SyscallResult? ThreadSync, string? Error);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, ulong a1, ulong a2, ulong a3,
        ulong a4, ulong a5, ulong a6);

    [DllImport("libc", EntryPoint = "_exit")]
    public static extern void ExitForkedChild(int status);

    public static SyscallResult Call(long number, ulong a1 = 0, ulong a2 = 0,
        ulong a3 = 0, ulong a4 = 0, ulong a5 = 0, ulong a6 = 0)
    {
        long value = Syscall(number, a1, a2, a3, a4, a5, a6);
        int error = Marshal.GetLastPInvokeError();
        return new(value, error);
    }

    public static ulong Pointer(IntPtr pointer) => unchecked((ulong)pointer.ToInt64());

    public static Instruction[] BuildFilter()
    {
        var result = new List<Instruction>
        {
            new(0x20, 0, 0, 4),                  // LD W ABS: seccomp_data.arch
            new(0x15, 1, 0, AuditArchX64),        // JEQ: reject other syscall ABIs.
            new(0x06, 0, 0, KillProcess),
            new(0x20, 0, 0, 0),                  // seccomp_data.nr
            new(0x45, 0, 1, 0x40000000),          // x32 syscall bit
            new(0x06, 0, 0, Denied),
            new(0x35, 0, 2, 512),                 // Old-kernel x32 alias range 512..547
            new(0x25, 1, 0, 547),
            new(0x06, 0, 0, Denied),
            new(0x15, 0, 1, 435),                 // clone3: force documented libc fallback.
            new(0x06, 0, 0, NotImplemented),
            new(0x15, 0, 6, 56),                  // clone: only ordinary threads.
            new(0x20, 0, 0, 16),                 // args[0] low word (clone flags)
            new(0x45, 0, 1, CloneNewNamespaces),
            new(0x06, 0, 0, Denied),
            new(0x45, 1, 0, CloneThread),
            new(0x06, 0, 0, Denied),
            new(0x06, 0, 0, Allow),
        };
        foreach (int number in DeniedSyscalls)
        {
            result.Add(new(0x15, 0, 1, (uint)number));
            result.Add(new(0x06, 0, 0, Denied));
        }
        result.Add(new(0x06, 0, 0, Allow));
        return result.ToArray();
    }

    // A small userspace control for the emitted filter; it never invokes seccomp.
    public static uint Evaluate(Instruction[] filter, uint arch, uint number, uint flags = 0)
    {
        uint accumulator = 0;
        for (int pc = 0; pc < filter.Length; pc++)
        {
            Instruction item = filter[pc];
            bool condition;
            switch (item.Code)
            {
                case 0x20:
                    accumulator = item.Value switch { 0 => number, 4 => arch, 16 => flags,
                        _ => throw new InvalidDataException("Unexpected filter load") };
                    break;
                case 0x06: return item.Value;
                case 0x15: condition = accumulator == item.Value; goto Jump;
                case 0x25: condition = accumulator > item.Value; goto Jump;
                case 0x35: condition = accumulator >= item.Value; goto Jump;
                case 0x45: condition = (accumulator & item.Value) != 0; goto Jump;
                default: throw new InvalidDataException("Unexpected filter opcode");
            }
            continue;
        Jump:
            pc += condition ? item.JumpTrue : item.JumpFalse;
        }
        throw new InvalidDataException("Filter did not return");
    }

    public static FilterCase[] AuditFilter(Instruction[] filter)
    {
        var cases = new List<FilterCase>();
        void Check(string name, uint arch, uint syscall, uint flags, uint expected)
        {
            uint actual = Evaluate(filter, arch, syscall, flags);
            cases.Add(new(name, actual, expected, actual == expected));
        }
        foreach (int nr in DeniedSyscalls) Check("denied_" + nr, AuditArchX64, (uint)nr, 0, Denied);
        Check("getpid_allowed", AuditArchX64, 39, 0, Allow);
        Check("clone_process_denied", AuditArchX64, 56, 17, Denied);
        Check("clone_thread_allowed", AuditArchX64, 56, 0x003D0F00, Allow);
        Check("clone_new_namespace_denied", AuditArchX64, 56, CloneThread | 0x10000000, Denied);
        Check("clone3_enosys", AuditArchX64, 435, 0, NotImplemented);
        Check("x32_bit_denied", AuditArchX64, 0x40000000 | 39, 0, Denied);
        Check("old_x32_512_denied", AuditArchX64, 512, 0, Denied);
        Check("old_x32_547_denied", AuditArchX64, 547, 0, Denied);
        Check("i386_arch_rejected", 0x40000003, 39, 0, KillProcess);
        Check("arm64_arch_rejected", 0xC00000B7, 39, 0, KillProcess);
        return cases.ToArray();
    }

    public static byte[] FilterBytes(Instruction[] filter)
    {
        var bytes = new byte[filter.Length * 8];
        for (int i = 0; i < filter.Length; i++)
        {
            Span<byte> item = bytes.AsSpan(i * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(item, filter[i].Code);
            item[2] = filter[i].JumpTrue;
            item[3] = filter[i].JumpFalse;
            BinaryPrimitives.WriteUInt32LittleEndian(item[4..], filter[i].Value);
        }
        return bytes;
    }

    public static Installation Install()
    {
        Instruction[] filter = BuildFilter();
        FilterCase[] cases = AuditFilter(filter);
        byte[] bytes = FilterBytes(filter);
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string architecture = RuntimeInformation.ProcessArchitecture.ToString();
        SyscallResult? noNewPrivileges = null, threadSync = null;
        string? error = null;
        IntPtr instructions = IntPtr.Zero, programPointer = IntPtr.Zero;
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("Only Linux little-endian x86_64 is supported");
            if (cases.Any(c => !c.Passed) || Marshal.SizeOf<FilterProgram>() != 16)
                throw new InvalidDataException("Filter audit or native ABI layout failed");
            instructions = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, instructions, bytes.Length);
            programPointer = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(new FilterProgram { Length = checked((ushort)filter.Length), Instructions = instructions }, programPointer, false);
            noNewPrivileges = Call(157, 38, 1); // prctl(PR_SET_NO_NEW_PRIVS, 1)
            if (noNewPrivileges.ReturnValue != 0) throw new InvalidOperationException("PR_SET_NO_NEW_PRIVS failed");
            threadSync = Call(317, 1, 1, Pointer(programPointer)); // SET_MODE_FILTER, TSYNC
            if (threadSync.ReturnValue != 0) throw new InvalidOperationException("SECCOMP TSYNC failed (positive TID also means failure)");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            if (programPointer != IntPtr.Zero) Marshal.FreeHGlobal(programPointer);
            if (instructions != IntPtr.Zero) Marshal.FreeHGlobal(instructions);
        }
        return new(error is null, architecture, hash, filter, cases, noNewPrivileges, threadSync, error);
    }
}
