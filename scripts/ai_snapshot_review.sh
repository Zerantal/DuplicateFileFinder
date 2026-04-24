#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <expected.png> <actual.png> <report.md> [model]" >&2
  exit 2
fi

expected_png="$1"
actual_png="$2"
report_path="$3"
model="${4:-${OPENAI_MODEL:-gpt-4.1-mini}}"
raw_response_path="${report_path%.md}.raw.json"
if [[ "$raw_response_path" == "$report_path" ]]; then
  raw_response_path="${report_path}.raw.json"
fi

if [[ ! -f "$expected_png" ]]; then
  echo "Expected image not found: $expected_png" >&2
  exit 2
fi

if [[ ! -f "$actual_png" ]]; then
  echo "Actual image not found: $actual_png" >&2
  exit 2
fi

if [[ -z "${OPENAI_API_KEY:-}" ]]; then
  echo "OPENAI_API_KEY is not set." >&2
  exit 2
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required for request/response JSON handling. Please install jq." >&2
  exit 2
fi

mkdir -p "$(dirname "$report_path")"

expected_b64="$(base64 -w 0 "$expected_png")"
actual_b64="$(base64 -w 0 "$actual_png")"

read -r -d '' prompt <<'EOF' || true
Compare the first image (expected) with the second image (actual) for a UI regression test.
Output concise markdown with these sections:
1) Summary
2) Visual Differences (layout, spacing, alignment, clipping, color, typography, states)
3) Likely Root Causes
4) Suggested Fixes

Rules:
- Mention approximate regions (header, row area, numeric columns, empty state, scrollbar gutter, etc.).
- Prioritize concrete differences over speculation.
- If no meaningful differences, say so explicitly.
EOF

response_file="$(mktemp)"
payload_file="$(mktemp)"
trap 'rm -f "$response_file" "$payload_file"' EXIT

jq -n \
  --arg model "$model" \
  --arg prompt "$prompt" \
  --arg expected_b64 "$expected_b64" \
  --arg actual_b64 "$actual_b64" \
  '{
    model: $model,
    input: [
      {
        role: "user",
        content: [
          { type: "input_text", text: $prompt },
          { type: "input_image", image_url: ("data:image/png;base64," + $expected_b64) },
          { type: "input_image", image_url: ("data:image/png;base64," + $actual_b64) }
        ]
      }
    ]
  }' > "$payload_file"

curl -sS https://api.openai.com/v1/responses \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "Content-Type: application/json" \
  --data-binary "@$payload_file" > "$response_file"

cp "$response_file" "$raw_response_path"

jq -r '
  .output_text
  // ([.output[]?.content[]? | select(.type=="output_text") | .text] | join("\n\n"))
  // empty
' "$response_file" > "$report_path"
if [[ ! -s "$report_path" ]]; then
  {
    echo "# AI Snapshot Review"
    echo
    echo "Could not extract output_text; raw JSON follows."
    echo
    echo '```json'
    cat "$response_file"
    echo '```'
  } > "$report_path"
fi

echo "Wrote AI snapshot review to $report_path"
echo "Wrote raw API response to $raw_response_path"
