param(
	[string]$NavisworksDir = 'C:\Program Files\Autodesk\Navisworks Manage 2024',
	[string]$OutDir = (Join-Path $PSScriptRoot '..\docs\api')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
[AppDomain]::CurrentDomain.add_AssemblyResolve({
	param($s, $e)
	$path = Join-Path $NavisworksDir ((New-Object Reflection.AssemblyName $e.Name).Name + '.dll')
	if (Test-Path $path) { [Reflection.Assembly]::LoadFrom($path) } else { $null }
})
$flags = [Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'
foreach ($name in 'Autodesk.Navisworks.Api', 'Autodesk.Navisworks.Clash', 'Autodesk.Navisworks.Timeliner', 'Autodesk.Navisworks.Takeoff') {
	$asm = [Reflection.Assembly]::LoadFrom((Join-Path $NavisworksDir "$name.dll"))
	try { $types = $asm.GetExportedTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
	$lines = foreach ($t in ($types | Sort-Object FullName)) {
		$base = if ($t.BaseType -and $t.BaseType.FullName -ne 'System.Object') { " : $($t.BaseType.Name)" } else { '' }
		"$($t.FullName)$base"
		if ($t.IsEnum) { '  = ' + ([Enum]::GetNames($t) -join ', '); continue }
		foreach ($m in $t.GetMembers($flags)) {
			switch ($m.MemberType) {
				'Property' { "  P $($m.PropertyType.Name) $($m.Name)" + $(if ($m.CanWrite -and $m.GetSetMethod()) { ' {set}' } else { '' }) }
				'Method' { if (-not $m.IsSpecialName) { "  M $($m.ReturnType.Name) $($m.Name)(" + (($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', ') + ')' } }
				'Constructor' { "  C (" + (($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', ') + ')' }
				'Event' { "  E $($m.Name)" }
				'Field' { if ($m.IsStatic) { "  F $($m.FieldType.Name) $($m.Name)" } }
			}
		}
	}
	$lines | Set-Content -Encoding utf8 (Join-Path $OutDir "$name.txt")
	"$name $($types.Count) types"
}
