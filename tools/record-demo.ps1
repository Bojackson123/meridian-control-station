#Requires -Version 5.1

<#
.SYNOPSIS
    Stops one aircraft on a cue, so the console's live -> stale -> lost transition can be recorded.

.DESCRIPTION
    The demo recording has to show a state *change*: a fleet flying, one aircraft going silent, and
    the console saying so on the map and in the panel at the same moment. That is a twenty-second
    sequence with two instants in it that matter, and both of them are decided by a clock rather
    than by anything visible -- so doing it by hand means starting a screen recorder, alt-tabbing to
    a terminal, finding a process id, and stopping it, all of which is in the recording.

    This script owns the timing and nothing else. It counts down loudly enough to arm a recorder,
    stops the aircraft, and then reports the two transitions *as the station reports them* -- by
    polling /api/vehicles rather than by assuming the thresholds -- so the run either produced the
    footage or says which part of it did not.

    It does not launch the fleet. tools/fleet-at-twelve.ps1 does that, holds the twelve open, and
    tears them down; teardown in two scripts is how a demo ends with aircraft still transmitting at
    a station that has moved on. Run that one in one terminal, this one in another.

    It also does not record the screen. A capture tool that has to be driven from PowerShell is a
    dependency on whichever one is installed, and the frame rate, the crop and the palette are
    decisions this script has no way to make well. ScreenToGif over the browser window at 8-10 fps
    is what the committed GIF was made with; the console steps at 4 Hz by design, so a higher rate
    buys duplicate frames and nothing else.

.PARAMETER ProcessId
    The aircraft to stop -- one of the process ids tools/fleet-at-twelve.ps1 printed. Omit it to
    have the running simulators listed and choose one; there is no default, because which marker
    sits clear of the others in the console's default view is a thing to look at rather than derive.

.PARAMETER ApiUrl
    The station. Defaults to the dev host's port, because the twelve-aircraft fleet is a set of host
    processes transmitting to 127.0.0.1:14550 and the compose stack publishes no UDP port at all --
    its single aircraft is a container on the internal network. A twelve-vehicle recording is
    therefore a `dotnet run --project src/Mcs.Api` recording by construction.

.PARAMETER ConsoleUrl
    The console, checked only so that a recording is not started against a page that was never
    serving. Defaults to Vite's port, to match ApiUrl.

.PARAMETER ArmSeconds
    Countdown before the aircraft is stopped. Long enough to start the recorder and get the pointer
    off the console, and it is also frames 1 and 2 of the storyboard -- the fleet flying, no chip
    anywhere -- which are what make the chip's arrival mean something.

.PARAMETER HoldSeconds
    How long to keep reporting after the vehicle reaches lost. The last frame is a still of the
    whole console with one dashed ring in it, and a GIF that cuts on the transition has no such
    frame in it to pause on.

.EXAMPLE
    ./tools/fleet-at-twelve.ps1                 # terminal 1, and leave it open
    ./tools/record-demo.ps1                     # terminal 2: lists the aircraft
    ./tools/record-demo.ps1 -ProcessId 24680    # terminal 2: arms, stops it, calls the transitions
#>

[CmdletBinding()]
param(
    [int] $ProcessId = 0,

    [string] $ApiUrl = 'http://localhost:5271',

    [string] $ConsoleUrl = 'http://localhost:5173',

    [ValidateRange(3, 60)]
    [int] $ArmSeconds = 8,

    [ValidateRange(0, 60)]
    [int] $HoldSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$simulatorProcessName = 'Mcs.Simulator'

function Get-Fleet {
    #  -UseBasicParsing because Windows PowerShell's default response parsing goes through the IE
    #  engine, which on a machine that has never had IE configured throws about its first-run
    #  wizard -- for a JSON body nothing needs a DOM for.
    $response = Invoke-WebRequest -Uri "$ApiUrl/api/vehicles" -UseBasicParsing -TimeoutSec 5
    return $response.Content | ConvertFrom-Json
}

#  Preflight, in the order the failures are worth telling apart. A recording started against a dead
#  API and one started against a healthy station with nothing flying at it look identical on screen
#  -- an empty map -- and the difference is only visible on playback, by which time the fleet has
#  been torn down.
try {
    $fleet = @(Get-Fleet)
}
catch {
    throw "No station answering at $ApiUrl/api/vehicles. Start it with " +
        "'dotnet run --project src/Mcs.Api' (it needs a Postgres; see the README), or pass -ApiUrl."
}

try {
    $null = Invoke-WebRequest -Uri "$ConsoleUrl/basemap/style.json" -UseBasicParsing -TimeoutSec 5
}
catch {
    throw "No console answering at $ConsoleUrl. Start it with 'cd web; npm run dev', or pass " +
        '-ConsoleUrl. The basemap style is what was asked for, because a console whose dev server ' +
        'is up but whose basemap 404s records as a black rectangle.'
}

if ($fleet.Count -eq 0) {
    throw "The station is up and no vehicle has reported. Start the fleet with " +
        "'./tools/fleet-at-twelve.ps1' and leave it running."
}

if ($fleet.Count -lt 12) {
    #  A warning rather than a failure: a shorter fleet still records the state change, and the one
    #  claim it loses is the storyboard's first frame -- that this runs at twelve rather than at two.
    Write-Warning ("$($fleet.Count) vehicles reporting, not twelve. The recording will show the " +
        'state change but not the fleet it was designed at.')
}

$aircraft = @(Get-Process -Name $simulatorProcessName -ErrorAction SilentlyContinue)

if ($aircraft.Count -eq 0) {
    throw "No $simulatorProcessName process is running on this machine, so there is nothing here " +
        "to stop -- though $($fleet.Count) vehicle(s) are reporting to $ApiUrl. If those are " +
        'containers or another host, this script cannot cue them.'
}

if ($ProcessId -eq 0) {
    Write-Host ''
    Write-Host "$($aircraft.Count) aircraft running. Look at the console, pick the marker that sits"
    Write-Host 'clear of the others, and pass its process id:'
    Write-Host ''
    $aircraft | Sort-Object StartTime | Format-Table Id, StartTime -AutoSize
    Write-Host '  ./tools/record-demo.ps1 -ProcessId <id>'
    Write-Host ''
    Write-Host 'The launch order is the system id order, so the first row is MAV-001.'
    return
}

if ($aircraft.Id -notcontains $ProcessId) {
    throw "Process $ProcessId is not one of the running $simulatorProcessName processes " +
        "($($aircraft.Id -join ', ')). Stopping the wrong process is a recording of nothing " +
        'happening, discovered on playback.'
}

#  Which vehicle this is, resolved by elimination rather than by asking the process: a system id
#  reaches the simulator as an environment variable, and reading another process's environment on
#  Windows needs either a debugger's access to its PEB or a WMI query that returns the command line
#  and not the environment. The station already knows the answer and will say so within a threshold.
$before = @($fleet | Where-Object { $_.state -ne 'Lost' } | ForEach-Object { $_.vehicleId })

Write-Host ''
Write-Host "Station  $ApiUrl -- $($fleet.Count) vehicles, $($before.Count) of them still reporting."
Write-Host "Console  $ConsoleUrl"
Write-Host "Stopping process $ProcessId in $ArmSeconds seconds."
Write-Host ''
Write-Host 'Start the recorder now. Crop to the console -- map, fleet panel and the bar across the'
Write-Host 'top, which is what says the station itself is healthy.'
Write-Host ''

foreach ($remaining in ($ArmSeconds..1)) {
    Write-Host "  $remaining"
    Start-Sleep -Seconds 1
}

Stop-Process -Id $ProcessId -Force
$elapsed = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ''
Write-Host '  STOPPED. Stale at ~3 s, lost at ~15 s, by the station clock.'
Write-Host ''

$staleAt = $null
$lostAt = $null
$subject = $null

#  Polled rather than timed. The thresholds are compile-time constants in Mcs.Core and this script
#  could restate them, but a restated constant is one that goes quietly wrong when the real one
#  moves -- and the interesting number is not when the threshold passed, it is when the *console*
#  could have shown it, which includes the fleet tick this poll is standing in for.
$deadline = [TimeSpan]::FromSeconds(40)

while ($elapsed.Elapsed -lt $deadline) {
    Start-Sleep -Milliseconds 250

    try { $fleet = @(Get-Fleet) } catch { continue }

    $quiet = @($fleet | Where-Object { $_.state -ne 'Live' -and $before -contains $_.vehicleId })

    if ($null -eq $staleAt -and $quiet.Count -gt 0) {
        $subject = $quiet[0].vehicleId
        $staleAt = $elapsed.Elapsed
        $live = @($fleet | Where-Object { $_.state -eq 'Live' }).Count
        Write-Host ("  {0,6:N1}s  STALE  {1}   -- {2} others still live" -f
            $staleAt.TotalSeconds, $subject, $live)
    }

    if ($null -ne $staleAt -and $null -eq $lostAt) {
        $it = $fleet | Where-Object { $_.vehicleId -eq $subject }

        if ($it -and $it.state -eq 'Lost') {
            $lostAt = $elapsed.Elapsed
            Write-Host ("  {0,6:N1}s  LOST   {1}   -- dashed ring, no heading" -f
                $lostAt.TotalSeconds, $subject)
            break
        }
    }
}

if ($null -eq $staleAt) {
    throw ("Forty seconds after stopping process $ProcessId, every vehicle the station knows about " +
        'is still live. Either that process was not one of the aircraft reporting to this station, ' +
        'or something else is transmitting on its system id.')
}

if ($null -eq $lostAt) {
    throw ("$subject went stale and had not reached lost forty seconds later. The recording is " +
        'half a sequence; the threshold is fifteen seconds, so something is still feeding that ' +
        'system id -- a second simulator on the same one is the usual cause.')
}

if ($HoldSeconds -gt 0) {
    Write-Host ''
    Write-Host "  Holding $HoldSeconds s on the full console -- one ring, the rest flying."
    Start-Sleep -Seconds $HoldSeconds
}

Write-Host ''
Write-Host '  Stop the recording.'
Write-Host ''
Write-Host ("  {0} went stale at {1:N1}s and lost at {2:N1}s." -f
    $subject, $staleAt.TotalSeconds, $lostAt.TotalSeconds)
Write-Host '  The two frames that have to read as stills are the first with an age chip and the'
Write-Host '  first with a dashed ring. If either needs the motion to make sense, record it again.'
Write-Host ''
