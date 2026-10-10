#!/usr/bin/env bash
# Regenerates this evidence directory from the exact pinned placeholder image. Read-only: it pulls and runs the
# image locally with overridden entrypoints to read files. It never pushes, never contacts Azure, and only copies
# files whose NAME is license-like (never key material or anything else).
# Usage: infra/evidence/placeholder-a265f55c/collect.sh   (needs a working Docker daemon)
set -euo pipefail
IMG='ghcr.io/mendhak/http-https-echo@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4'
OUT="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
run() { docker run --rm --entrypoint "$@"; }

{
  echo "image: $IMG"
  docker image inspect "$IMG" --format 'platform: {{.Os}}/{{.Architecture}}
created: {{.Created}}
user: {{.Config.User}}
entrypoint: {{.Config.Entrypoint}}
cmd: {{.Config.Cmd}}
workdir: {{.Config.WorkingDir}}
exposed_ports: {{.Config.ExposedPorts}}
image_id: {{.Id}}'
  echo; echo "== os-release"; run sh "$IMG" -c 'cat /etc/os-release'
  echo; echo "== node / yarn"; run sh "$IMG" -c 'node -v; yarn -v'
  echo; echo "== docker history (newest first)"; docker history --no-trunc --format '{{.CreatedBy}}' "$IMG" | cut -c1-240
} > "$OUT/image-identity.txt"

# OS layer: declared licenses straight from the apk database (METADATA DECLARATIONS ONLY: the image ships no license documents for these packages)
run sh "$IMG" -c 'cat /lib/apk/db/installed' > "$tmp/apk-db.txt"
awk -v RS= -F'\n' 'BEGIN{print "package\tversion\tdeclared_license"} {p="";v="";l=""; for(i=1;i<=NF;i++){ if($i ~ /^P:/)p=substr($i,3); if($i ~ /^V:/)v=substr($i,3); if($i ~ /^L:/)l=substr($i,3)} if(p!="") print p"\t"v"\t"l}' "$tmp/apk-db.txt" | { read -r h; echo "$h"; sort; } > "$OUT/os-packages.tsv"

# Application dependencies: declared license (package.json) and whether a license-like FILE exists in the package directory
run node "$IMG" -e '
const fs=require("fs"),path=require("path"),rows=[];
const isLic=n=>/^(licen[cs]e|copying|notice)/i.test(n);
function walk(dir){ for(const e of fs.readdirSync(dir,{withFileTypes:true})){ if(!e.isDirectory()) continue; const p=path.join(dir,e.name);
  if(e.name.startsWith("@")){ walk(p); continue; }
  const pj=path.join(p,"package.json");
  if(fs.existsSync(pj)){ try{ const j=JSON.parse(fs.readFileSync(pj,"utf8")); let l=j.license; if(l&&typeof l==="object") l=l.type; if(!l&&Array.isArray(j.licenses)) l=j.licenses.map(x=>x.type||x).join(" OR ");
    const has=fs.readdirSync(p).some(isLic); rows.push([j.name,j.version,l||"UNDECLARED",has?"yes":"no",p.replace("/app/node_modules/","")].join("\t")); }catch(_){ rows.push([e.name,"?","UNPARSEABLE","?",p].join("\t")); } }
  const nm=path.join(p,"node_modules"); if(fs.existsSync(nm)) walk(nm); } }
walk("/app/node_modules");
console.log("package\tversion\tdeclared_license\tlicense_file_present\tpath"); console.log(rows.sort().join("\n"));' > "$OUT/app-packages.tsv"

# License documents that actually exist in the image (names filtered; nothing else is copied)
rm -rf "$OUT/licenses"; mkdir -p "$OUT/licenses/app" "$OUT/licenses/node"
run sh "$IMG" -c 'cd /app/node_modules && find . -maxdepth 4 -type f \( -iname "LICENSE" -o -iname "LICENSE.*" -o -iname "LICENCE*" -o -iname "COPYING*" -o -iname "NOTICE*" -o -iname "LICENSE-*" \) -print0 | xargs -0 tar -cf -' | tar -xf - -C "$OUT/licenses/app"
run cat "$IMG" /usr/local/LICENSE > "$OUT/licenses/node/LICENSE"

# Presence (only) of the bundled TLS test key: record that it exists, never its bytes
{ echo "files in /app that are key material (names only, bytes NOT collected):"; run sh "$IMG" -c 'ls -1 /app | grep -i -E "\.(pem|key|crt)$" || true'; } > "$OUT/bundled-key-material.txt"

# Guard: refuse to leave key-like content behind
if grep -rIl -E 'BEGIN (RSA |EC |OPENSSL |ENCRYPTED )?PRIVATE KEY' "$OUT" >/dev/null 2>&1 || find "$OUT" -iname '*.pem' | grep -q .; then
  echo "key-like content found in evidence output; aborting" >&2; exit 1; fi
echo "evidence written to $OUT"
