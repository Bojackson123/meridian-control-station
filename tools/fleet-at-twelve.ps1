#Requires -Version 5.1

<#
.SYNOPSIS
    Flies twelve aircraft at a locally running station, so the console can be looked at full.

.DESCRIPTION
    A development harness, not part of the stack. The store has been built for twelve vehicles
    since the first week and the console's layout was designed at twelve, but until this existed
    neither had ever been *seen* at twelve -- and the panel's fit, the marker overlap at the
    default zoom and the event rate are all cheap to check now and embarrassing to discover in a
    demo.

    Twelve processes rather than one process with twelve aircraft, because that is what the
    simulator is: one aircraft, one MAVLink system id, one socket. Twelve of them arriving at one
    bound port is also the case the adapter's framing has to survive, which an in-process fleet
    would not exercise at all.

    Each aircraft is given the same twelve-point circuit rotated by its index, so the twelve start
    evenly spaced around it and stay that way -- they fly at one speed, so the spacing is preserved
    rather than maintained. Every dart should lie along the circle. A fleet visibly flying crabwise
    is a heading that is out by a constant, which is the classic way to get this wrong and is
    otherwise invisible without reading numbers off the panel.

    Start the API first. This transmits into a UDP socket and neither knows nor can discover
    whether anything is listening.

.PARAMETER Count
    How many aircraft. Twelve is the store's ceiling and the number the layout was designed at;
    a thirteenth is rejected by the store, which is worth watching once on purpose.

    Four is the floor, and it is the simulator's own configuration that sets it: appsettings.json
    defines a four-waypoint square, the route below is supplied as environment overrides, and the
    configuration binder *merges* the two by index. A fleet route of three points therefore leaves
    the square's fourth corner in play, and the aircraft fly three points of a circle and then a
    leg to a corner half a kilometre away. For a single aircraft, run the simulator directly:
    cd src/Mcs.Simulator; dotnet run.

.PARAMETER TargetPort
    The station's UDP port. Matches the adapter's default.

.EXAMPLE
    ./tools/fleet-at-twelve.ps1

    Then stop one of them -- the script prints a process id per vehicle -- and watch that row and
    its marker cross into stale and then lost while the other eleven keep flying.
#>

[CmdletBinding()]
param(
    #  Four, not one: see .PARAMETER Count. A shorter route does not replace the simulator's own,
    #  it overlays it, and the leftover waypoint is invisible until an aircraft flies at it.
    [ValidateRange(4, 24)]
    [int] $Count = 12,

    [ValidateRange(1, 65535)]
    [int] $TargetPort = 14550
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The circuit, matching the one aircraft's own: a 400 m circle about the Huntsville origin the
# simulator's appsettings.json flies a square around. Twelve waypoints rather than four, so that
# twelve aircraft can each start at one.
$originLatitude = 34.7304
$originLongitude = -86.5861
$radiusMeters = 400.0
$altitudeMeters = 300.0

# The fleet flies slower than the single aircraft's 22 m/s, and that is forced rather than chosen.
# Twelve waypoints on a 400 m circle are 207 m apart, the capture radius is derived at 1.5 turn
# radii, and the follower refuses a route whose shortest leg is under twice its capture radius --
# under that the aircraft is inside the next waypoint the moment it reaches this one and flies none
# of the route. At 22 m/s the turn radius is 106 m and the check fails by a wide margin; at 15 m/s
# it is 49 m and the same circuit is comfortable.
#
# The alternatives were a 613 m circle, which does not fit the console's default view, and a
# hand-set capture radius a few metres above the turn radius, which is the boundary the follower
# documents as the one where floating-point luck decides whether a waypoint is captured at all.
$cruiseSpeedMetersPerSecond = 15.0

$metersPerDegreeLatitude = 111320.0
$metersPerDegreeLongitude = $metersPerDegreeLatitude * [Math]::Cos($originLatitude * [Math]::PI / 180.0)

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/Mcs.Simulator'

# Built once, here, so that launching twelve aircraft is twelve process starts and no compilation.
# It is also what produces the executable the launch below needs; twelve `dotnet run` invocations
# would each consider building, and racing each other over one obj/ directory fails in ways that
# read as compiler bugs.
Write-Host 'Building the simulator...'
& dotnet build $project -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

# The executable itself, launched below instead of `dotnet run`.
#
# `dotnet run` starts the application as a *child* process and waits on it, so the pid this script
# recorded was the launcher's. Stopping that on Windows leaves the aircraft flying -- there is no
# job object taking the tree with it -- which is exactly the orphaned simulator the teardown at the
# bottom exists to prevent, transmitting at a station that has moved on with nothing left to find it
# by but the process list. Launching the built executable makes the recorded pid the aircraft's own.
#
# Found rather than composed from a hardcoded path, so the target framework lives in the project
# file alone. Newest wins, since a stale build under an older framework would otherwise be picked.
$executable = Get-ChildItem -Path (Join-Path $project 'bin/Release') -Filter 'Mcs.Simulator.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $executable) {
    throw "The build succeeded but no Mcs.Simulator.exe was found under $project/bin/Release."
}

$waypoints = @(0..($Count - 1) | ForEach-Object {
    $bearing = 2.0 * [Math]::PI * $_ / $Count

    # North and east from the centre, then back to degrees. A flat-earth projection over 400 m,
    # which is the same approximation the simulator's own LocalProjection makes.
    [pscustomobject]@{
        Latitude  = $originLatitude + ($radiusMeters * [Math]::Cos($bearing)) / $metersPerDegreeLatitude
        Longitude = $originLongitude + ($radiusMeters * [Math]::Sin($bearing)) / $metersPerDegreeLongitude
    }
})

$launched = @()

try {
    foreach ($index in 0..($Count - 1)) {
        $systemId = $index + 1

        $env:Simulator__SystemId = $systemId
        $env:Simulator__TargetPort = $TargetPort
        $env:Simulator__CruiseSpeedMetersPerSecond = $cruiseSpeedMetersPerSecond

        # The same circuit, rotated so this aircraft begins at its own waypoint. The simulator
        # starts at route[0], so the rotation is the whole of the spacing.
        foreach ($offset in 0..($Count - 1)) {
            $waypoint = $waypoints[($index + $offset) % $Count]

            Set-Item -Path "env:Simulator__Route__${offset}__LatitudeDegrees" -Value $waypoint.Latitude
            Set-Item -Path "env:Simulator__Route__${offset}__LongitudeDegrees" -Value $waypoint.Longitude
            Set-Item -Path "env:Simulator__Route__${offset}__AltitudeMetersMsl" -Value $altitudeMeters
        }

        # Inherits the environment set just above, which is why these are set per iteration rather
        # than passed as arguments -- the configuration binder reads the environment and there is no
        # command line for a route.
        #
        # -WorkingDirectory is load-bearing. Host.CreateApplicationBuilder takes its content root
        # from the working directory, so a simulator started from the repository root finds no
        # appsettings.json, binds an empty Route, and fails startup validation on a file sitting
        # right beside its own project.
        $process = Start-Process -FilePath $executable.FullName `
            -WorkingDirectory $project `
            -PassThru -WindowStyle Hidden

        $launched += [pscustomobject]@{ Vehicle = 'MAV-{0:D3}' -f $systemId; ProcessId = $process.Id }
    }

    $launched | Format-Table -AutoSize

    Write-Host ''
    Write-Host "$Count aircraft transmitting to 127.0.0.1:$TargetPort."
    Write-Host 'Stop one with  Stop-Process -Id <id>  to watch a vehicle go stale and then lost.'
    Write-Host ''

    # Held open rather than detached, so there is one thing to close and no orphaned aircraft left
    # transmitting at a station that has moved on. A simulator nobody remembers starting is a
    # vehicle on the console nobody can account for.
    $null = Read-Host 'Press Enter to stop them all'
}
finally {
    # In finally, not after the prompt. A non-interactive host makes Read-Host throw rather than
    # wait, and every failure that leaves this script early otherwise leaves twelve aircraft flying
    # at a station with no way left to find them but the process list.
    foreach ($entry in $launched) {
        try { Stop-Process -Id $entry.ProcessId -Force -ErrorAction Stop } catch {}
    }

    if ($launched.Count -gt 0) { Write-Host 'Stopped.' }
}
