# ZIP 归档辅助函数，只由 scripts\publish-release.ps1 dot-source。
# 这个文件本身**不随包分发**（不在 publish-release.ps1 的 $scriptFiles 白名单里），
# 因此不会改变 payload 内容；包内 install/upgrade/repair 逻辑不依赖它。
#
# 存在理由：Compress-Archive（Windows PowerShell 5.1）写出的条目用 '\\' 作分隔符，
# 而 ZIP 规范（APPNOTE 4.4.17.1）要求 '/'。用 '\\' 的包在 Linux/macOS/部分解压工具里
# 会被当成一整串带反斜杠的文件名，而不是目录树。所以发布包里先压缩、再把条目名归一成
# '/'，并在写校验和之前断言结构正确。

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 读 zip 中央目录里条目名的原始字节。ZipArchiveEntry.FullName 在 Windows 上会把分隔符
# 统一归成 '\\'，看不到真实存储的字节，所以这里定位 EOCD 再逐条扫中央目录。
# 返回 [pscustomobject]@{ Name; Utf8Flag } 的数组。
function Get-ZipEntryNamesRaw {
    param([Parameter(Mandatory)][string]$ArchivePath)
    $bytes = [System.IO.File]::ReadAllBytes($ArchivePath)
    if ($bytes.Length -lt 22) { throw "Archive is too small: $ArchivePath" }

    $eocd = -1
    $lowerBound = [Math]::Max(0, $bytes.Length - 22 - 65535)
    for ($i = $bytes.Length - 22; $i -ge $lowerBound; $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and
            $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) {
            $eocd = $i
            break
        }
    }
    if ($eocd -lt 0) { throw "End of central directory record not found: $ArchivePath" }

    $entryCount = [int][BitConverter]::ToUInt16($bytes, $eocd + 10)
    $position = [int][BitConverter]::ToUInt32($bytes, $eocd + 16)
    $entries = New-Object 'System.Collections.Generic.List[object]'
    for ($n = 0; $n -lt $entryCount; $n++) {
        if (-not ($bytes[$position] -eq 0x50 -and $bytes[$position + 1] -eq 0x4B -and
                  $bytes[$position + 2] -eq 0x01 -and $bytes[$position + 3] -eq 0x02)) {
            throw "Central directory record #$n is malformed in $ArchivePath"
        }
        $flags = [BitConverter]::ToUInt16($bytes, $position + 8)
        $nameLength = [int][BitConverter]::ToUInt16($bytes, $position + 28)
        $extraLength = [int][BitConverter]::ToUInt16($bytes, $position + 30)
        $commentLength = [int][BitConverter]::ToUInt16($bytes, $position + 32)
        $name = [System.Text.Encoding]::UTF8.GetString($bytes, $position + 46, $nameLength)
        $entries.Add([pscustomobject]@{
            Name = $name
            Utf8Flag = (($flags -band 0x800) -ne 0)
        })
        $position += 46 + $nameLength + $extraLength + $commentLength
    }
    return $entries.ToArray()
}

# 把 zip 的条目分隔符从 '\\' 归一成 '/'（规范要求），逐条复制内容与时间戳。
function Repair-ZipEntrySeparators {
    param(
        [Parameter(Mandatory)][string]$SourceArchive,
        [Parameter(Mandatory)][string]$DestinationArchive
    )
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $source = [System.IO.Compression.ZipFile]::OpenRead($SourceArchive)
    try {
        if (Test-Path -LiteralPath $DestinationArchive) {
            Remove-Item -LiteralPath $DestinationArchive -Force
        }
        $target = [System.IO.Compression.ZipFile]::Open($DestinationArchive, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $source.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                if ($name.EndsWith('/')) {
                    # 目录条目：CreateFromDirectory 会给空目录也写一条
                    $target.CreateEntry($name) | Out-Null
                    continue
                }
                $newEntry = $target.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
                $newEntry.LastWriteTime = $entry.LastWriteTime
                $input = $entry.Open()
                try {
                    $output = $newEntry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                }
                finally { $input.Dispose() }
            }
        }
        finally { $target.Dispose() }
    }
    finally { $source.Dispose() }
}

# 断言发布 ZIP 的目录结构：
#   - 条目名纯 ASCII、用 '/' 分隔、不以 '/' 开头、不含 '..' 或盘符；
#   - 顶层只能是 $AllowedTopLevel 里列出的目录/文件；
#   - $RequiredEntries 全部存在；
#   - 条目集合（除 release-manifest.json 自排除）与 manifest 的文件清单逐项一致。
function Assert-ReleaseZipStructure {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][object]$Manifest,
        [string[]]$RequiredEntries = @(),
        [string[]]$AllowedTopLevel = @('App', 'Guardian', 'scripts', 'docs', 'release-manifest.json')
    )
    $entries = @(Get-ZipEntryNamesRaw -ArchivePath $ArchivePath)
    if ($entries.Count -eq 0) { throw "Archive has no entries: $ArchivePath" }

    $failures = @()
    $names = @()
    $fileNames = @()
    $topLevel = @()
    foreach ($entry in $entries) {
        $name = $entry.Name
        $names += $name
        if ($name -notmatch '^[\x20-\x7E]+$') {
            $failures += "Entry name is not pure ASCII: $name"
        }
        if ($name.Contains('\')) {
            $failures += "Entry name uses a backslash separator: $name"
        }
        if ($name.StartsWith('/')) {
            $failures += "Entry name is rooted with '/': $name"
        }
        $segments = @($name -split '/')
        if ($segments -contains '..') {
            $failures += "Entry name escapes the archive root: $name"
        }
        if ($segments[0] -match '^[A-Za-z]:$') {
            $failures += "Entry name starts with a drive letter: $name"
        }
        if ($AllowedTopLevel -notcontains $segments[0]) {
            $failures += "Entry '$name' is not under one of: $($AllowedTopLevel -join ', ')"
        }
        elseif ($topLevel -notcontains $segments[0]) {
            $topLevel += $segments[0]
        }
        if (-not $name.EndsWith('/')) { $fileNames += $name }
    }
    if ($failures.Count -gt 0) {
        throw "Archive structure is invalid ($ArchivePath): $($failures -join '; ')"
    }

    foreach ($required in $RequiredEntries) {
        if ($names -notcontains $required) {
            throw "Archive is missing the required entry '$required' ($ArchivePath)"
        }
    }

    $manifestPaths = @($Manifest.Files | ForEach-Object { ([string]$_.RelativePath).Replace('\', '/') })
    $entryFiles = @($fileNames | Where-Object { $_ -ne 'release-manifest.json' })
    $missing = @($manifestPaths | Where-Object { $entryFiles -notcontains $_ })
    $extra = @($entryFiles | Where-Object { $manifestPaths -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        $detail = @()
        if ($missing.Count -gt 0) { $detail += "missing from archive: $($missing -join ', ')" }
        if ($extra.Count -gt 0) { $detail += "not in manifest: $($extra -join ', ')" }
        throw "Archive and release manifest disagree ($ArchivePath): $($detail -join '; ')"
    }
    if ($entryFiles.Count -ne $manifestPaths.Count) {
        throw "Archive entry count $($entryFiles.Count) does not match manifest file count $($manifestPaths.Count) ($ArchivePath)"
    }

    return [pscustomobject]@{
        EntryCount = $names.Count
        FileCount = $entryFiles.Count
        TopLevel = @($topLevel)
    }
}
