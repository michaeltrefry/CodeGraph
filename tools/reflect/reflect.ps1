$dll = "C:\Users\MTrefry\.nuget\packages\anthropic\10.4.0\lib\net9.0\Anthropic.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$out = @()

try {
    $types = $asm.GetTypes()
} catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
}

$types = $types | Where-Object { $_.Name -like "*ContentBlockDelta*" -or $_.Name -like "*InputJson*" }
foreach ($t in $types) {
    $out += "TYPE: $($t.FullName)"
    try {
        $t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance) |
            ForEach-Object { $out += "  METHOD: $($_.Name)" }
        $t.GetProperties() | ForEach-Object { $out += "  PROP: $($_.PropertyType.Name) $($_.Name)" }
    } catch { $out += "  (error: $_)" }
    $out += ""
}

$out | Set-Content "d:\repos\TC.CodeGraphApi\tools\reflect\ps_out.txt" -Encoding UTF8
