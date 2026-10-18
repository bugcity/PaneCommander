param([Parameter(Mandatory)][string]$ProbeDirectory,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$probePath=[IO.Path]::GetFullPath($ProbeDirectory,$repo)
$outPath=[IO.Path]::GetFullPath($Output,$repo)
if(Test-Path -LiteralPath $outPath){throw 'Use a fresh evidence output'}
[IO.Directory]::CreateDirectory($outPath)|Out-Null
$ready=Get-Content (Join-Path $probePath 'content-rendered-not-physical-present.json') -Raw|ConvertFrom-Json
$owned=Get-CimInstance Win32_Process -Filter ('ProcessId='+$ready.ProcessId)
if(-not $owned -or $owned.Name -ne 'PaneCommander.DrawOnlyProbe.exe' -or -not $owned.CommandLine.Contains($probePath)){throw 'Owned diagnostic process identity mismatch'}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ReadOnlyDxgiAdapters {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]public struct Desc {
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;
  public uint VendorId,DeviceId,SubSysId,Revision;public UIntPtr DedicatedVideoMemory,DedicatedSystemMemory,SharedSystemMemory;
  public uint LuidLow;public int LuidHigh;public uint Flags;
 }
 public sealed class Info {public string Description{get;set;}public string LuidPrefix{get;set;}public uint VendorId{get;set;}public uint DeviceId{get;set;}public ulong DedicatedVideoMemory{get;set;}public ulong SharedSystemMemory{get;set;}public uint Flags{get;set;}}
 [DllImport("dxgi.dll")]static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int EnumAdapter(IntPtr self,uint index,out IntPtr adapter);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int GetDesc(IntPtr self,out Desc desc);
 static T Method<T>(IntPtr p,int slot)where T:Delegate{return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));}
 public static Info[] Read(){IntPtr factory=IntPtr.Zero;var result=new List<Info>();
  try{var iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387");Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid,out factory));
   for(uint i=0;i<16;i++){IntPtr adapter=IntPtr.Zero;var hr=Method<EnumAdapter>(factory,12)(factory,i,out adapter);if(hr==unchecked((int)0x887a0002))break;
    try{Marshal.ThrowExceptionForHR(hr);Desc desc;Marshal.ThrowExceptionForHR(Method<GetDesc>(adapter,10)(adapter,out desc));result.Add(new Info{Description=desc.Description,LuidPrefix=String.Format("luid_0x{0:X8}_0x{1:X8}",unchecked((uint)desc.LuidHigh),desc.LuidLow),VendorId=desc.VendorId,DeviceId=desc.DeviceId,DedicatedVideoMemory=desc.DedicatedVideoMemory.ToUInt64(),SharedSystemMemory=desc.SharedSystemMemory.ToUInt64(),Flags=desc.Flags});}
    finally{if(adapter!=IntPtr.Zero)Marshal.Release(adapter);}}
   return result.ToArray();
  }finally{if(factory!=IntPtr.Zero)Marshal.Release(factory);}
 }
}
'@
$result=[ordered]@{CollectedUtc=[DateTimeOffset]::UtcNow.ToString('o');Product=$false;ProcessId=$ready.ProcessId;SessionId=$owned.SessionId;ReadOnly=$true;GpuAssignmentChanged=$false;OsSettingsChanged=$false;GuiCaptureCalled=$false;UiAutomationCalled=$false}
$result.DisplayDrivers=@(Get-CimInstance Win32_PnPSignedDriver -Filter "DeviceClass='DISPLAY'"|Select-Object DeviceName,DriverVersion,DriverDate,Manufacturer,InfName,IsSigned)
try{$result.DxgiAdapters=@([ReadOnlyDxgiAdapters]::Read())}catch{$result.DxgiError=$_.Exception.Message}
try{$process=Get-Process -Id $ready.ProcessId;$result.Modules=@($process.Modules|Where-Object{$_.ModuleName -match '^(d3d|dxgi|dwm|milcore|wpfgfx|Presentation|igd|nv)'}|ForEach-Object{[pscustomobject]@{Name=$_.ModuleName;Version=$_.FileVersionInfo.FileVersion;Path=$_.FileName}})}catch{$result.ModuleError=$_.Exception.Message}
$dwmProcesses=@(Get-CimInstance Win32_Process -Filter "Name='dwm.exe'"|Where-Object{$_.SessionId -eq $owned.SessionId})
$result.DwmProcesses=@($dwmProcesses|Select-Object ProcessId,SessionId)
$targetPids=@([int]$ready.ProcessId)+@($dwmProcesses|ForEach-Object{[int]$_.ProcessId})
$samples=@()
for($sampleIndex=0;$sampleIndex -lt 1;$sampleIndex++){
 $sample=[ordered]@{Utc=[DateTimeOffset]::UtcNow.ToString('o')}
 foreach($class in @('Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine')){
  try{
   $filter=($targetPids|ForEach-Object{"Name LIKE 'pid_$($_)_%'"}) -join ' OR '
   $values=@(Get-CimInstance -ClassName $class -Filter $filter)
   $sample.GpuEngines=@($values|Select-Object Name,UtilizationPercentage)
  }catch{$sample[$class+'Error']=$_.Exception.Message}
 }
 $samples+=,[pscustomobject]$sample
}
$result.Samples=$samples
$result.Scope='GPU engine LUID and module provenance only; no heap/private/working-set/GPU-allocation sampling.'
$result.InterpretationLimit='Counter adapter LUIDs identify observed engine instances. Modules alone do not prove the complete per-frame API path or fault owner. Idle zero utilization does not mean software rendering.'
$result|ConvertTo-Json -Depth 10|Set-Content (Join-Path $outPath 'gpu-route.json')
