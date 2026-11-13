#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $GameDirectory,
    [string] $PatchedDll = (Join-Path $PSScriptRoot '../out/Assembly-CSharp.dll')
)

$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameDirectory).ProviderPath
$patched = (Resolve-Path -LiteralPath $PatchedDll).ProviderPath
$managed = Join-Path $game "Sophie's Dice_Data/Managed"
$mono = Join-Path $game 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
$config = Join-Path $game 'MonoBleedingEdge/etc'
$testDll = Join-Path (Split-Path $patched) 'SophieDiceCngVerification.exe'

$syntax = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Verify.cs')))
$references = [Microsoft.CodeAnalysis.MetadataReference[]] @(
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $managed 'mscorlib.dll'))
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $managed 'System.dll'))
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $managed 'UnityEngine.CoreModule.dll'))
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($patched)
)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'Verify', [Microsoft.CodeAnalysis.SyntaxTree[]] @($syntax), $references, $options)
$stream = [IO.File]::Create($testDll)
try {
    $emit = $compilation.Emit($stream)
    if (!$emit.Success) {
        throw ($emit.Diagnostics -join [Environment]::NewLine)
    }
}
finally {
    $stream.Dispose()
}

# Run the test executable in the same Mono build the game embeds.
if (-not ('UnityMonoTest' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class UnityMonoTest
{
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_set_dirs(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string assemblies,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string config);

    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mono_jit_init_version(string name, string version);

    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mono_domain_assembly_open(IntPtr domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);

    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, IntPtr argv);

    public static int Run(string mono, string managed, string config, string executable)
    {
        IntPtr library = NativeLibrary.Load(mono);
        NativeLibrary.SetDllImportResolver(typeof(UnityMonoTest).Assembly, (name, assembly, path) => library);
        mono_set_dirs(managed, config);
        IntPtr domain = mono_jit_init_version("DicePatchVerification", "v4.0.30319");
        if (domain == IntPtr.Zero)
            throw new InvalidOperationException("Unable to initialize Unity Mono");

        IntPtr test = mono_domain_assembly_open(domain, executable);
        if (test == IntPtr.Zero)
            throw new InvalidOperationException("Unable to load verification executable");

        IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr file = Marshal.StringToCoTaskMemUTF8(executable);
        try
        {
            Marshal.WriteIntPtr(argv, file);
            return mono_jit_exec(domain, test, 1, argv);
        }
        finally
        {
            Marshal.FreeCoTaskMem(file);
            Marshal.FreeHGlobal(argv);
        }
    }
}
'@
}
$previousMonoPath = $env:MONO_PATH
try {
    $env:MONO_PATH = (Split-Path $patched) + [IO.Path]::PathSeparator + $managed
    $result = [UnityMonoTest]::Run($mono, $managed, $config, $testDll)
}
finally {
    $env:MONO_PATH = $previousMonoPath
}
exit $result
