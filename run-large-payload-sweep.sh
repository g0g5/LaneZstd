#!/usr/bin/env bash

set -u

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPORT_PATH="${ROOT_DIR}/large-payload-sweep-results.txt"

BENCH_DURATION_SECONDS="${BENCH_DURATION_SECONDS:-30}"
BENCH_WARMUP_SECONDS="${BENCH_WARMUP_SECONDS:-3}"
SWEEP_MESSAGES_PER_SECOND="${SWEEP_MESSAGES_PER_SECOND:-50}"
RECEIVE_QUEUE_CAPACITY="${RECEIVE_QUEUE_CAPACITY:-256}"
RECEIVE_WORKER_COUNT="${RECEIVE_WORKER_COUNT:-1}"
SEED="${SEED:-424242}"

SIZES=(768 1024 1152 1280 1408 1472 1536 2048 3072 4096)
MAX_PACKET_SIZES=(9999 1200)
CONTROL_CASES=("1024:200" "2048:100" "4096:50")

extract_metric() {
  local line="$1"
  local key="$2"

  if [[ "$line" =~ (^|[[:space:]])${key}=([^[:space:]]+) ]]; then
    printf '%s' "${BASH_REMATCH[2]}"
  else
    printf 'n/a'
  fi
}

last_matching_line() {
  local text="$1"
  local pattern="$2"
  awk -v pattern="$pattern" '$0 ~ pattern { line = $0 } END { print line }' <<<"$text"
}

write_case_block() {
  local suite="$1"
  local size="$2"
  local rate="$3"
  local max_packet="$4"
  local exit_code="$5"
  local bench_line="$6"
  local edge_line="$7"
  local hub_line="$8"
  local integrity_line="$9"
  local edge_stats_line="${10}"
  local hub_stats_line="${11}"

  {
    printf 'case suite=%s payload=%sB rate=%s/s max_packet=%s exit=%s\n' "$suite" "$size" "$rate" "$max_packet" "$exit_code"
    printf '%s\n' "$bench_line"
    printf '%s\n' "$edge_line"
    printf '%s\n' "$hub_line"
    printf '%s\n' "$integrity_line"
    printf '%s\n' "$edge_stats_line"
    printf '%s\n' "$hub_stats_line"
    printf '\n'
  } >>"$REPORT_PATH"
}

append_table_row() {
  local suite="$1"
  local size="$2"
  local rate="$3"
  local max_packet="$4"
  local exit_code="$5"
  local edge_line="$6"
  local hub_line="$7"
  local integrity_line="$8"
  local edge_stats_line="$9"
  local hub_stats_line="${10}"

  local total_lost
  local edge_lost
  local hub_lost
  local edge_source_in
  local hub_source_in
  local edge_queue_drop
  local hub_queue_drop
  local edge_oversize
  local hub_oversize
  local edge_encode_us
  local hub_encode_us

  total_lost="$(extract_metric "$integrity_line" "lost")"
  edge_lost="$(extract_metric "$edge_line" "lost")"
  hub_lost="$(extract_metric "$hub_line" "lost")"
  edge_source_in="$(extract_metric "$edge_line" "source_in")"
  hub_source_in="$(extract_metric "$hub_line" "source_in")"
  edge_queue_drop="$(extract_metric "$edge_stats_line" "queue_drop")"
  hub_queue_drop="$(extract_metric "$hub_stats_line" "queue_drop")"
  edge_oversize="$(extract_metric "$edge_stats_line" "drop_oversize")"
  hub_oversize="$(extract_metric "$hub_stats_line" "drop_oversize")"
  edge_encode_us="$(extract_metric "$edge_line" "src_encode_us_avg")"
  hub_encode_us="$(extract_metric "$hub_line" "src_encode_us_avg")"

  printf '%-8s %-7s %-7s %-10s %-4s %-10s %-10s %-10s %-10s %-10s %-11s %-11s %-12s %-12s %-13s %-13s\n' \
    "$suite" "$size" "$rate" "$max_packet" "$exit_code" "$total_lost" "$edge_lost" "$hub_lost" "$edge_source_in" "$hub_source_in" "$edge_queue_drop" "$hub_queue_drop" "$edge_oversize" "$hub_oversize" "$edge_encode_us" "$hub_encode_us" >>"$REPORT_PATH"
}

run_case() {
  local suite="$1"
  local size="$2"
  local rate="$3"
  local max_packet="$4"

  local output
  local exit_code
  local bench_line
  local edge_line
  local hub_line
  local integrity_line
  local edge_stats_line
  local hub_stats_line

  printf 'Running suite=%s payload=%sB rate=%s/s max_packet=%s\n' "$suite" "$size" "$rate" "$max_packet"

  set +e
  output="$(dotnet run --no-build --project "${ROOT_DIR}/src/LaneZstd.Cli/LaneZstd.Cli.csproj" -- bench \
    --duration-seconds "$BENCH_DURATION_SECONDS" \
    --warmup-seconds "$BENCH_WARMUP_SECONDS" \
    --messages-per-second "$rate" \
    --avg-payload-bytes "$size" \
    --min-payload-bytes "$size" \
    --max-payload-bytes "$size" \
    --max-packet-size "$max_packet" \
    --stats-interval 0 \
    --receive-queue-capacity "$RECEIVE_QUEUE_CAPACITY" \
    --receive-worker-count "$RECEIVE_WORKER_COUNT" \
    --validate integrity \
    --seed "$SEED" \
    --output text 2>&1)"
  exit_code=$?
  set -e

  bench_line="$(last_matching_line "$output" '^bench ')"
  edge_line="$(last_matching_line "$output" '^edge->hub ')"
  hub_line="$(last_matching_line "$output" '^hub->edge ')"
  integrity_line="$(last_matching_line "$output" '^integrity ')"
  edge_stats_line="$(last_matching_line "$output" '^edge stats final ')"
  hub_stats_line="$(last_matching_line "$output" '^hub stats final ')"

  append_table_row "$suite" "$size" "$rate" "$max_packet" "$exit_code" "$edge_line" "$hub_line" "$integrity_line" "$edge_stats_line" "$hub_stats_line"
  write_case_block "$suite" "$size" "$rate" "$max_packet" "$exit_code" "$bench_line" "$edge_line" "$hub_line" "$integrity_line" "$edge_stats_line" "$hub_stats_line"
}

main() {
  : >"$REPORT_PATH"

  {
    printf 'LaneZstd large payload sweep\n'
    printf 'generated_at=%s\n' "$(date -Iseconds)"
    printf 'duration_seconds=%s warmup_seconds=%s queue_capacity=%s worker_count=%s seed=%s\n' \
      "$BENCH_DURATION_SECONDS" "$BENCH_WARMUP_SECONDS" "$RECEIVE_QUEUE_CAPACITY" "$RECEIVE_WORKER_COUNT" "$SEED"
    printf 'size_points=%s\n' "${SIZES[*]}"
    printf 'max_packet_sizes=%s\n' "${MAX_PACKET_SIZES[*]}"
    printf 'control_cases=%s\n\n' "${CONTROL_CASES[*]}"
    printf '%-8s %-7s %-7s %-10s %-4s %-10s %-10s %-10s %-10s %-10s %-11s %-11s %-12s %-12s %-13s %-13s\n' \
      'suite' 'sizeB' 'rate' 'max_pkt' 'exit' 'total_lost' 'e2h_lost' 'h2e_lost' 'e2h_srcin' 'h2e_srcin' 'edge_qdrop' 'hub_qdrop' 'edge_oversz' 'hub_oversz' 'e2h_enc_us' 'h2e_enc_us'
  } >>"$REPORT_PATH"

  for max_packet in "${MAX_PACKET_SIZES[@]}"; do
    for size in "${SIZES[@]}"; do
      run_case "sweep" "$size" "$SWEEP_MESSAGES_PER_SECOND" "$max_packet"
    done
  done

  for control_case in "${CONTROL_CASES[@]}"; do
    IFS=':' read -r size rate <<<"$control_case"
    run_case "control" "$size" "$rate" 9999
  done

  printf 'Report written to %s\n' "$REPORT_PATH"
}

set -e
main "$@"
