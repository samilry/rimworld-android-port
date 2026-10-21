# Frames of a tester's video as JPEG files, through Windows' own media stack (no ffmpeg on this PC).
# usage: powershell -File host/frames.ps1 <video.mp4> <output folder> [count, default 12] [start seconds] [end seconds]
param([string]$Video, [string]$OutDir, [int]$Count = 12, [double]$From = -1, [double]$To = -1)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($operation, [Type]$type)
{
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    $task.Result
}
[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Editing.MediaComposition, Windows.Media.Editing, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Editing.MediaClip, Windows.Media.Editing, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.ImageStream, Windows.Graphics, ContentType = WindowsRuntime] | Out-Null
New-Item -ItemType Directory -Force $OutDir | Out-Null
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync((Resolve-Path $Video).Path)) ([Windows.Storage.StorageFile])
$clip = Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($file)) ([Windows.Media.Editing.MediaClip])
$composition = New-Object Windows.Media.Editing.MediaComposition
# (through the interface: PowerShell 5.1 shows neither the projection's Add nor IVector's Append on the object)
[System.Collections.Generic.ICollection[Windows.Media.Editing.MediaClip]].GetMethod('Add').Invoke($composition.Clips, @($clip)) | Out-Null
$seconds = $clip.OriginalDuration.TotalSeconds
$start = if ($From -ge 0) { $From } else { 0 }
$end = if ($To -ge 0) { [Math]::Min($To, $seconds) } else { $seconds }
"duration {0:F1} s, {1} frames from {2:F1} to {3:F1} s" -f $seconds, $Count, $start, $end
for ($i = 0; $i -lt $Count; $i++)
{
    $at = $start + ($end - $start) * ($i + 0.5) / $Count
    $image = Await ($composition.GetThumbnailAsync([TimeSpan]::FromSeconds($at), 0, 0, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream])
    $source = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($image)
    $path = Join-Path $OutDir ("frame-{0:D2}-{1:F1}s.jpg" -f $i, $at)
    $target = [System.IO.File]::Create($path)
    $source.CopyTo($target)
    $target.Close()
    $source.Close()
    $path
}
