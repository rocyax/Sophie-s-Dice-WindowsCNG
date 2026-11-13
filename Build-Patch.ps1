#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManagedDirectory,
    [Parameter(Mandatory)]
    [string] $DnSpyDirectory,
    [string] $OriginalDll,
    [string] $OutputDll = (Join-Path $PSScriptRoot 'out/Assembly-CSharp.dll')
)

$ErrorActionPreference = 'Stop'
$live = (Resolve-Path -LiteralPath $ManagedDirectory).ProviderPath
$liveDll = Join-Path $live 'Assembly-CSharp.dll'
if (-not $OriginalDll) {
    $backup = "$liveDll.original-20755425.bak"
    $OriginalDll = $liveDll
    if (Test-Path -LiteralPath $backup -PathType Leaf) {
        $OriginalDll = $backup
    }
}
$original = (Resolve-Path -LiteralPath $OriginalDll).ProviderPath
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDll)
$bin = Join-Path $DnSpyDirectory 'bin'
$originalHash = '61344E314F20E97CEB40886A4F576AB20E7EFAFC3A5295EE428967A62428CEE3'
if ((Get-FileHash -LiteralPath $original).Hash -ne $originalHash) {
    throw 'Unsupported input DLL. Use the unmodified Assembly-CSharp.dll from Steam build 20755425.'
}
if ($output -eq $original -or $output -eq $liveDll) {
    throw 'Choose an output path outside the input and live DLL paths.'
}

$dnlib = Join-Path $bin 'dnlib.dll'
foreach ($dependency in 'dnlib', 'dnSpy.Contracts.Logic', 'dnSpy.Contracts.DnSpy') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin "$dependency.dll"))
}
$editor = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'dnSpy.AsmEditor.x.dll'))

$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp10)
$syntax = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
    [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CryptoRand.cs')), $parseOptions)
$references = [Microsoft.CodeAnalysis.MetadataReference[]] @(
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $live 'mscorlib.dll'))
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $live 'UnityEngine.CoreModule.dll'))
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($original)
)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'SophiesDice.CngPatch', [Microsoft.CodeAnalysis.SyntaxTree[]] @($syntax), $references, $options)
$helper = [IO.MemoryStream]::new()
try {
    $emit = $compilation.Emit($helper)
    if (!$emit.Success) {
        throw ($emit.Diagnostics -join [Environment]::NewLine)
    }
    $patchReferences = @($dnlib, 'System.Runtime.dll', 'System.Collections.dll', 'System.Linq.dll', 'System.Console.dll')
    if (-not ('DicePatch' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Patch.cs') -ReferencedAssemblies $patchReferences -CompilerOptions '/nowarn:1701'
    }

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    $stage = "$output.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [DicePatch]::Apply($original, $stage, $helper.ToArray(), $editor)
        [IO.File]::Move($stage, $output, $true)
    }
    finally {
        [IO.File]::Delete($stage)
    }
    Get-FileHash -LiteralPath $output
}
finally {
    $helper.Dispose()
}
