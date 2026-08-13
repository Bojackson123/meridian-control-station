#!/usr/bin/env bash
#
# Flies twelve aircraft at a locally running station, so the console can be looked at full.
#
# A development harness, not part of the stack. The store has been built for twelve vehicles since
# the first week and the console's layout was designed at twelve, but until this existed neither had
# ever been *seen* at twelve -- and the panel's fit, the marker overlap at the default zoom and the
# event rate are all cheap to check now and embarrassing to discover in a demo.
#
# Twelve processes rather than one process with twelve aircraft, because that is what the simulator
# is: one aircraft, one MAVLink system id, one socket. Twelve of them arriving at one bound port is
# also the case the adapter's framing has to survive, which an in-process fleet would not exercise
# at all.
#
# Each aircraft is given the same twelve-point circuit rotated by its index, so the twelve start
# evenly spaced around it and stay that way -- they fly at one speed, so the spacing is preserved
# rather than maintained. Every dart should lie along the circle. A fleet visibly flying crabwise is
# a heading that is out by a constant, which is the classic way to get this wrong and is otherwise
# invisible without reading numbers off the panel.
#
# Start the API first. This transmits into a UDP socket and neither knows nor can discover whether
# anything is listening.
#
# tools/fleet-at-twelve.ps1 is the same harness for Windows PowerShell; a change here belongs there
# in the same commit.
#
#     ./tools/fleet-at-twelve.sh
#
# Then kill one of the printed process ids and watch that row and its marker cross into stale and
# then lost while the other eleven keep flying.

set -euo pipefail

COUNT="${1:-12}"
TARGET_PORT="${2:-14550}"

# The circuit, matching the one aircraft's own: a 400 m circle about the Huntsville origin the
# simulator's appsettings.json flies a square around. Twelve waypoints rather than four, so that
# twelve aircraft can each start at one.
ORIGIN_LATITUDE=34.7304
ORIGIN_LONGITUDE=-86.5861
RADIUS_METERS=400
ALTITUDE_METERS=300

# The fleet flies slower than the single aircraft's 22 m/s, and that is forced rather than chosen.
# Twelve waypoints on a 400 m circle are 207 m apart, the capture radius is derived at 1.5 turn
# radii, and the follower refuses a route whose shortest leg is under twice its capture radius --
# under that the aircraft is inside the next waypoint the moment it reaches this one and flies none
# of the route. At 22 m/s the turn radius is 106 m and the check fails by a wide margin; at 15 m/s it
# is 49 m and the same circuit is comfortable.
#
# The alternatives were a 613 m circle, which does not fit the console's default view, and a hand-set
# capture radius a few metres above the turn radius, which is the boundary the follower documents as
# the one where floating-point luck decides whether a waypoint is captured at all.
CRUISE_SPEED_MPS=15

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Mcs.Simulator"

# Built once, then run twelve times without rebuilding. Twelve concurrent `dotnet run` invocations
# race each other over one obj/ directory and fail in ways that read as compiler bugs.
echo 'Building the simulator...'
dotnet build "$project" -c Release --nologo >/dev/null

# North and east from the centre, then back to degrees. A flat-earth projection over 400 m, which is
# the same approximation the simulator's own LocalProjection makes. awk rather than bash arithmetic
# because these are not integers and there is no floating point in the shell.
waypoints="$(awk -v count="$COUNT" -v lat="$ORIGIN_LATITUDE" -v lon="$ORIGIN_LONGITUDE" \
    -v radius="$RADIUS_METERS" 'BEGIN {
        pi = atan2(0, -1)
        metres_per_degree_latitude = 111320
        metres_per_degree_longitude = metres_per_degree_latitude * cos(lat * pi / 180)

        for (i = 0; i < count; i++) {
            bearing = 2 * pi * i / count
            printf "%.7f %.7f\n", \
                lat + (radius * cos(bearing)) / metres_per_degree_latitude, \
                lon + (radius * sin(bearing)) / metres_per_degree_longitude
        }
    }')"

mapfile -t waypoint_lines <<< "$waypoints"

pids=()

# Held open rather than detached, so there is one thing to close and no orphaned aircraft left
# transmitting at a station that has moved on. A simulator nobody remembers starting is a vehicle on
# the console nobody can account for.
stop_them_all() {
    for pid in "${pids[@]}"; do
        kill "$pid" 2>/dev/null || true
    done

    echo 'Stopped.'
}

trap stop_them_all EXIT INT TERM

for ((index = 0; index < COUNT; index++)); do
    system_id=$((index + 1))

    route=()

    # The same circuit, rotated so this aircraft begins at its own waypoint. The simulator starts at
    # route[0], so the rotation is the whole of the spacing.
    for ((offset = 0; offset < COUNT; offset++)); do
        read -r waypoint_latitude waypoint_longitude <<< "${waypoint_lines[$(((index + offset) % COUNT))]}"

        route+=(
            "Simulator__Route__${offset}__LatitudeDegrees=$waypoint_latitude"
            "Simulator__Route__${offset}__LongitudeDegrees=$waypoint_longitude"
            "Simulator__Route__${offset}__AltitudeMetersMsl=$ALTITUDE_METERS"
        )
    done

    #  The configuration binder reads the environment, and there is no command line for a route.
    #
    #  Started from inside the project directory, which is load-bearing: `dotnet run --project`
    #  leaves the working directory where it was and Host.CreateApplicationBuilder takes its content
    #  root from there, so a simulator launched from the repository root finds no appsettings.json,
    #  binds an empty Route, and fails startup validation on a file sitting beside its own project.
    (
        cd "$project"
        env "Simulator__SystemId=$system_id" "Simulator__TargetPort=$TARGET_PORT" \
            "Simulator__CruiseSpeedMetersPerSecond=$CRUISE_SPEED_MPS" "${route[@]}" \
            dotnet run -c Release --no-build >/dev/null
    ) &

    pids+=("$!")

    printf 'MAV-%03d  %s\n' "$system_id" "$!"
done

echo
echo "Twelve aircraft transmitting to 127.0.0.1:$TARGET_PORT."
echo 'Kill one with  kill <pid>  to watch a vehicle go stale and then lost.'
echo 'Ctrl-C stops them all.'
echo

wait
