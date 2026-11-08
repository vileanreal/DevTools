#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$repo_root/src/DevTools.Web/sample-logs"
api_dir="$output_dir/api"
worker_dir="$output_dir/worker"
database_dir="$output_dir/database"

mkdir -p "$api_dir" "$worker_dir" "$database_dir"

for file_number in $(seq 1 12); do
  file_path="$api_dir/api-2026-11-$(printf '%02d' "$file_number").log"
  : > "$file_path"
  for entry in $(seq 1 1000); do
    day=$((1 + (file_number - 1) % 8))
    hour=$(((entry / 3600 + file_number * 2) % 24))
    minute=$(((entry / 60 + file_number * 7) % 60))
    second=$((entry % 60))
    millis=$(((entry * 37) % 1000))
    request_id="req-$(printf '%02d-%05d' "$file_number" "$entry")"
    duration=$((12 + (entry * 17) % 2800))
    if (( entry % 197 == 0 )); then
      level="ERROR"; message="Checkout request failed requestId=$request_id status=500 durationMs=$duration exception=PaymentGatewayTimeout"
    elif (( entry % 47 == 0 )); then
      level="WARN"; message="Slow API response requestId=$request_id route=/api/orders durationMs=$duration retry=1"
    elif (( entry % 19 == 0 )); then
      level="DEBUG"; message="Request diagnostics requestId=$request_id cacheHit=false tenant=demo-$((entry % 12))"
    else
      level="INFO"; message="Request completed requestId=$request_id method=$([ $((entry % 3)) -eq 0 ] && printf POST || printf GET) route=/api/$([ $((entry % 2)) -eq 0 ] && printf orders || printf products) status=200 durationMs=$duration"
    fi
    printf '2026-11-%02dT%02d:%02d:%02d.%03dZ [%s] %s\n' "$day" "$hour" "$minute" "$second" "$millis" "$level" "$message" >> "$file_path"
    if (( entry % 197 == 0 )); then
      printf '   at SampleCommerce.Api.Payments.PaymentClient.AuthorizeAsync(String requestId)\n   at SampleCommerce.Api.Orders.CheckoutHandler.HandleAsync()\n' >> "$file_path"
    fi
  done
done

for file_number in $(seq 1 10); do
  file_path="$worker_dir/worker-2026-11-$(printf '%02d' "$file_number").jsonl"
  : > "$file_path"
  for entry in $(seq 1 900); do
    day=$((1 + (file_number - 1) % 8))
    hour=$(((entry / 3600 + file_number * 3) % 24))
    minute=$(((entry / 60 + file_number * 5) % 60))
    second=$((entry % 60))
    job_id="job-$(printf '%02d-%05d' "$file_number" "$entry")"
    queue="$(if (( entry % 3 == 0 )); then printf invoices; elif (( entry % 3 == 1 )); then printf notifications; else printf inventory; fi)"
    if (( entry % 163 == 0 )); then
      level="ERROR"; message="Background job failed after retry limit"
    elif (( entry % 41 == 0 )); then
      level="WARN"; message="Background job retry scheduled"
    elif (( entry % 13 == 0 )); then
      level="DEBUG"; message="Worker heartbeat and queue diagnostics"
    else
      level="INFO"; message="Background job completed successfully"
    fi
    printf '{"timestamp":"2026-11-%02dT%02d:%02d:%02dZ","level":"%s","message":"%s jobId=%s queue=%s attempt=%d","jobId":"%s","queue":"%s"}\n' "$day" "$hour" "$minute" "$second" "$level" "$message" "$job_id" "$queue" "$((1 + entry % 4))" "$job_id" "$queue" >> "$file_path"
  done
done

for file_number in $(seq 1 8); do
  file_path="$database_dir/database-2026-11-$(printf '%02d' "$file_number").log"
  : > "$file_path"
  for entry in $(seq 1 900); do
    day=$((1 + (file_number - 1) % 8))
    hour=$(((entry / 3600 + file_number * 4) % 24))
    minute=$(((entry / 60 + file_number * 9) % 60))
    second=$((entry % 60))
    elapsed=$((4 + (entry * 23) % 4200))
    connection=$((100 + (entry % 80)))
    if (( entry % 211 == 0 )); then
      level="FATAL"; message="Database connection pool exhausted connection=$connection waitMs=$elapsed"
    elif (( entry % 89 == 0 )); then
      level="ERROR"; message="Database command timeout connection=$connection command=UpdateInventory elapsedMs=$elapsed"
    elif (( entry % 37 == 0 )); then
      level="WARN"; message="Slow database query connection=$connection command=GetOrderHistory elapsedMs=$elapsed rows=$((entry % 5000))"
    else
      level="INFO"; message="Database command completed connection=$connection command=LoadProduct elapsedMs=$elapsed rows=$((1 + entry % 40))"
    fi
    printf '2026-11-%02d %02d:%02d:%02d | %s | %s\n' "$day" "$hour" "$minute" "$second" "$level" "$message" >> "$file_path"
  done
done

total_lines=$(find "$output_dir" -type f \( -name '*.log' -o -name '*.jsonl' \) -exec wc -l {} + | awk 'END {print $1}')
total_files=$(find "$output_dir" -type f \( -name '*.log' -o -name '*.jsonl' \) | wc -l | tr -d ' ')
printf 'Generated %s physical lines across %s files in %s\n' "$total_lines" "$total_files" "$output_dir"
