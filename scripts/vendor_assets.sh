#!/usr/bin/env bash
set -euo pipefail

# Vendor assets locally into wwwroot/lib
# Usage: ./scripts/vendor_assets.sh

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="$ROOT_DIR/wwwroot/lib/bootstrap-icons"
CSS_URL="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.10.5/font/bootstrap-icons.css"
SIGNALR_URL="https://cdn.jsdelivr.net/npm/@microsoft/signalr@7.0.11/dist/browser/signalr.min.js"

# jQuery + DataTables (Bootstrap5 integration)
JQUERY_URL="https://code.jquery.com/jquery-3.6.4.min.js"
DATATABLES_JS_URL="https://cdn.datatables.net/1.13.6/js/jquery.dataTables.min.js"
DATATABLES_BS_JS_URL="https://cdn.datatables.net/1.13.6/js/dataTables.bootstrap5.min.js"
DATATABLES_BS_CSS_URL="https://cdn.datatables.net/1.13.6/css/dataTables.bootstrap5.min.css"
DATATABLES_COLREORDER_JS_URL="https://cdn.datatables.net/colreorder/1.6.2/js/dataTables.colReorder.min.js"
# DataTables Buttons extension and dependencies
DT_BUTTONS_JS_URL="https://cdn.datatables.net/buttons/2.4.1/js/dataTables.buttons.min.js"
DT_BUTTONS_BS_JS_URL="https://cdn.datatables.net/buttons/2.4.1/js/buttons.bootstrap5.min.js"
DT_BUTTONS_CSS_URL="https://cdn.datatables.net/buttons/2.4.1/css/buttons.bootstrap5.min.css"
DT_BUTTONS_HTML5_JS_URL="https://cdn.datatables.net/buttons/2.4.1/js/buttons.html5.min.js"
DT_BUTTONS_PRINT_JS_URL="https://cdn.datatables.net/buttons/2.4.1/js/buttons.print.min.js"
DT_BUTTONS_COLVIS_JS_URL="https://cdn.datatables.net/buttons/2.4.1/js/buttons.colVis.min.js"
JSZIP_URL="https://cdnjs.cloudflare.com/ajax/libs/jszip/3.10.1/jszip.min.js"
PDFMAKE_URL="https://cdnjs.cloudflare.com/ajax/libs/pdfmake/0.2.7/pdfmake.min.js"
PDFMAKE_VFS_URL="https://cdnjs.cloudflare.com/ajax/libs/pdfmake/0.2.7/vfs_fonts.js"

mkdir -p "$OUT_DIR"
cd "$OUT_DIR"

echo "Downloading bootstrap-icons CSS..."
curl -sSfL "$CSS_URL" -o bootstrap-icons.css

# Ensure fonts directory exists
mkdir -p fonts

# Extract relative font filenames from CSS, strip query strings and download them
# Use sed to robustly capture the part after ./fonts/ up to a quote, ? or )
FONT_URLS=$(grep -o '\./fonts/[^\")?]*' bootstrap-icons.css | sed 's#\./fonts/##' | sed 's/\?.*$//')

for f in $FONT_URLS; do
  [ -z "$f" ] && continue
  fname=$(basename "$f" | tr -d '\r\n"')
  echo "Downloading font: $fname"
  curl -sSfL "https://cdn.jsdelivr.net/npm/bootstrap-icons@1.10.5/font/fonts/$fname" -o "fonts/$fname"
done

# Rewrite CSS font URLs to point to local /lib path and remove any query strings
# Use a portable sed invocation (double-quoted to allow embedded single quotes)
sed -E -i.bak "s#url\\((\"|' )?\\./fonts/([^)\"']+)(\\?[^)\"']*)?(\"|' )?\\)#url(\"/lib/bootstrap-icons/fonts/\\2\")#g" bootstrap-icons.css || true

echo "Downloading SignalR script..."
mkdir -p "$ROOT_DIR/wwwroot/lib/signalr"
curl -sSfL "$SIGNALR_URL" -o "$ROOT_DIR/wwwroot/lib/signalr/signalr.min.js" || true

echo "Downloading jQuery and DataTables (Bootstrap5 integration)..."
mkdir -p "$ROOT_DIR/wwwroot/lib/jquery"
mkdir -p "$ROOT_DIR/wwwroot/lib/datatables/js"
mkdir -p "$ROOT_DIR/wwwroot/lib/datatables/css"

curl -sSfL "$JQUERY_URL" -o "$ROOT_DIR/wwwroot/lib/jquery/jquery.min.js" || true
curl -sSfL "$DATATABLES_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/jquery.dataTables.min.js" || true
curl -sSfL "$DATATABLES_BS_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/dataTables.bootstrap5.min.js" || true
curl -sSfL "$DATATABLES_BS_CSS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/css/dataTables.bootstrap5.min.css" || true
curl -sSfL "$DATATABLES_COLREORDER_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/dataTables.colReorder.min.js" || true

# Buttons + dependencies
curl -sSfL "$DT_BUTTONS_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/dataTables.buttons.min.js" || true
curl -sSfL "$DT_BUTTONS_BS_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/buttons.bootstrap5.min.js" || true
curl -sSfL "$DT_BUTTONS_CSS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/css/buttons.bootstrap5.min.css" || true
curl -sSfL "$DT_BUTTONS_HTML5_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/buttons.html5.min.js" || true
curl -sSfL "$DT_BUTTONS_PRINT_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/buttons.print.min.js" || true
curl -sSfL "$DT_BUTTONS_COLVIS_JS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/buttons.colVis.min.js" || true
curl -sSfL "$JSZIP_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/jszip.min.js" || true
curl -sSfL "$PDFMAKE_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/pdfmake.min.js" || true
curl -sSfL "$PDFMAKE_VFS_URL" -o "$ROOT_DIR/wwwroot/lib/datatables/js/vfs_fonts.js" || true

echo "Done. Files saved to:"
echo " - $OUT_DIR/bootstrap-icons.css"
echo " - $OUT_DIR/fonts/"
echo " - $ROOT_DIR/wwwroot/lib/signalr/signalr.min.js"
echo " - $ROOT_DIR/wwwroot/lib/jquery/jquery.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/jquery.dataTables.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/dataTables.bootstrap5.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/dataTables.buttons.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/buttons.bootstrap5.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/buttons.html5.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/buttons.print.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/buttons.colVis.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/jszip.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/pdfmake.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/vfs_fonts.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/js/dataTables.colReorder.min.js"
echo " - $ROOT_DIR/wwwroot/lib/datatables/css/dataTables.bootstrap5.min.css"
echo " - $ROOT_DIR/wwwroot/lib/datatables/css/buttons.bootstrap5.min.css"

echo "Remember to commit wwwroot/lib/bootstrap-icons and wwwroot/lib/signalr to your repo if you want these vendored."
