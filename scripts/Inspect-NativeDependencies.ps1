param([Parameter(Mandatory)][string]$Directory, [string]$ReportPath = '')
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
if (-not ('ShunshouBuild.PeImports' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
namespace ShunshouBuild {
 public sealed class Import { public string Name; public bool Delayed; }
 public sealed class PeInfo { public int Machine; public bool Managed; public List<Import> Imports = new List<Import>(); }
 public static class PeImports {
  public static PeInfo Read(string path) {
   var b=File.ReadAllBytes(path); if(b.Length<64 || b[0]!=77 || b[1]!=90)return null;
   int pe=BitConverter.ToInt32(b,60); if(pe<0 || pe+24>=b.Length)return null;
   int count=BitConverter.ToUInt16(b,pe+6), size=BitConverter.ToUInt16(b,pe+20), optional=pe+24;
   bool x64=BitConverter.ToUInt16(b,optional)==0x20b; int directories=optional+(x64?112:96);
   var result=new PeInfo {Machine=BitConverter.ToUInt16(b,pe+4), Managed=BitConverter.ToUInt32(b,directories+14*8)!=0};
   Func<uint,int> map=(rva)=>{ for(int i=0;i<count;i++){int s=optional+size+i*40; uint va=BitConverter.ToUInt32(b,s+12), length=Math.Max(BitConverter.ToUInt32(b,s+8),BitConverter.ToUInt32(b,s+16)); if(rva>=va && rva<va+length)return checked((int)(rva-va+BitConverter.ToUInt32(b,s+20)));} return checked((int)rva); };
   Func<int,string> read=(offset)=>{int end=offset;while(end<b.Length && b[end]!=0)end++;return Encoding.ASCII.GetString(b,offset,end-offset);};
   foreach(int index in new[]{1,13}) { uint rva=BitConverter.ToUInt32(b,directories+index*8); if(rva==0)continue; int offset=map(rva), stride=index==1?20:32;
    for(int i=0;i<10000 && offset+stride<=b.Length;i++,offset+=stride){uint name=BitConverter.ToUInt32(b,offset+(index==1?12:4)); if(name==0)break; result.Imports.Add(new Import {Name=read(map(name)),Delayed=index==13});}
   }
   return result;
  }
 }
}
'@
}
$files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object { $_.Extension -in @('.dll','.exe') })
$records = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $pe = [ShunshouBuild.PeImports]::Read($file.FullName)
    if (-not $pe) { continue }
    $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\','/')
    if (-not $pe.Managed -and $pe.Machine -ne 0x8664) { $failures.Add("Non-x64 native binary: $relative") }
    foreach ($dependency in $pe.Imports) {
        $name = $dependency.Name
        $local = Test-Path -LiteralPath (Join-Path $file.DirectoryName $name)
        $root = Test-Path -LiteralPath (Join-Path $Directory $name)
        $contract = $name -match '^(?i)(api-ms-|ext-ms-)'
        $system = Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $name)
        $vc = $name -match '^(?i)(msvcp|vcruntime|concrt|vccorlib|vcomp|vcamp)\d.*\.dll$'
        $status = if ($local -or $root) { 'app-local' } elseif ($contract) { 'Windows API contract' } elseif ($system -and -not $vc) { 'Windows system DLL on build machine' } else { 'missing' }
        if ($status -eq 'missing' -and (-not $dependency.Delayed -or $vc)) { $failures.Add("Missing dependency: $relative -> $name") }
        $records.Add([ordered]@{ File=$relative; Dependency=$name; Delayed=$dependency.Delayed; Resolution=$status })
    }
}
$report = [ordered]@{
    Architecture='win-x64'; ExaminedFiles=$files.Count; RequiredMissing=$failures.ToArray(); Imports=$records.ToArray()
    Limitation='Static import-table audit. Windows API contracts and system DLL availability still require testing on a clean supported Windows installation; dynamically loaded dependencies are not exhaustively covered.'
}
if ($ReportPath) { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8 }
if ($failures.Count -gt 0) { throw ($failures -join "`n") }
Write-Host "Native dependency audit passed for $($files.Count) PE candidates; C++ runtime imports resolve inside the portable directory."
