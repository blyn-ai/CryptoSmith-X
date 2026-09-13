#!/bin/sh
# Fetch a rendered /studio/v2/<ASSET> page from the TEST studio, retrying until the grid is really in the
# body. Under load the page's coverage query can time out and the studio answers an empty 500; a check
# that read that body would report "no rows" rather than anything about the venues.
#   ops/v2_fetch.sh BTC | ops/v2_dashes.py dYdX
asset="$1"
for attempt in 1 2 3 4 5 6; do
  body=$(ssh -o ConnectTimeout=25 csx-prod "curl -s --max-time 180 'http://10.8.0.1:7778/studio/v2/$asset?probe=$(date +%s%N)'")
  case "$body" in
    *'class="v2-row"'*) printf '%s' "$body"; exit 0 ;;
  esac
  echo "attempt $attempt: no rendered grid, retrying" >&2
  sleep 5
done
exit 1
