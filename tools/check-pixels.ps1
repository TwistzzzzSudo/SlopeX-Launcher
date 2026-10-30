param(
    [Parameter(Mandatory = $true)][string]$Image,
    [string]$Points = "300,100;900,400;1180,690;1188,700"
)

Add-Type -AssemblyName System.Drawing

$bmp = [System.Drawing.Bitmap]::FromFile($Image)
Write-Output ("SIZE=" + $bmp.Width + "x" + $bmp.Height)

foreach ($p in ($Points -split ';')) {
    $xy = $p -split ','
    $x = [int]$xy[0]; $y = [int]$xy[1]
    if ($x -ge $bmp.Width -or $y -ge $bmp.Height) { continue }
    $c = $bmp.GetPixel($x, $y)
    Write-Output ("({0},{1}) = #{2:X2}{3:X2}{4:X2}" -f $x, $y, $c.R, $c.G, $c.B)
}

$bmp.Dispose()
