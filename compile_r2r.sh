#!/usr/bin/env bash

set -euo pipefail
wd=$(dirname "$0")

cd src/fscr
dotnet publish -p:PublishReadyToRun=true --ucr
