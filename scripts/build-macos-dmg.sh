#!/usr/bin/env bash
set -euo pipefail

# Build a macOS DMG for Rejector.Avalonia.
# Usage examples:
#   scripts/build-macos-dmg.sh
#   scripts/build-macos-dmg.sh --version 1.0.36
#   scripts/build-macos-dmg.sh --rid osx-x64 --version 1.0.36

APP_NAME="Rejector"
PROJECT="src/Rejector.Avalonia/Rejector.Avalonia.csproj"
OUT_DIR="artifacts/macos"
RID="$(uname -m)"
VERSION=""
BUNDLE_ID="com.photon1503.rejector"
SELF_CONTAINED="true"
DMG_README="docs/macos-dmg-readme.txt"

usage() {
  cat <<EOF
Build a macOS DMG for this repo.

Options:
  --version <x.y.z>        App version for DMG name and Info.plist (required)
  --rid <osx-arm64|osx-x64> Runtime identifier (default: detected from host)
  --app-name <name>        App and DMG display name (default: Rejector)
  --bundle-id <id>         macOS bundle identifier (default: com.photon1503.rejector)
  --project <path>         Project path (default: src/Rejector.Avalonia/Rejector.Avalonia.csproj)
  --out-dir <path>         Output root directory (default: artifacts/macos)
  --self-contained <bool>  true/false (default: true)
  -h, --help               Show help

Examples:
  scripts/build-macos-dmg.sh --version 1.0.36
  scripts/build-macos-dmg.sh --version 1.0.36 --rid osx-x64
EOF
}

detect_rid() {
  case "$RID" in
    arm64|aarch64) RID="osx-arm64" ;;
    x86_64|amd64) RID="osx-x64" ;;
    osx-arm64|osx-x64) ;;
    *)
      echo "Unsupported host architecture: $RID"
      echo "Pass --rid osx-arm64 or --rid osx-x64 explicitly."
      exit 1
      ;;
  esac
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      VERSION="${2:-}"
      shift 2
      ;;
    --rid)
      RID="${2:-}"
      shift 2
      ;;
    --app-name)
      APP_NAME="${2:-}"
      shift 2
      ;;
    --bundle-id)
      BUNDLE_ID="${2:-}"
      shift 2
      ;;
    --project)
      PROJECT="${2:-}"
      shift 2
      ;;
    --out-dir)
      OUT_DIR="${2:-}"
      shift 2
      ;;
    --self-contained)
      SELF_CONTAINED="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1"
      usage
      exit 1
      ;;
  esac
done

if [[ -z "$VERSION" ]]; then
  echo "Missing required --version"
  usage
  exit 1
fi

detect_rid

PUBLISH_DIR="$OUT_DIR/publish-$RID"
APP_DIR="$OUT_DIR/$APP_NAME.app"
DMG_STAGE="$OUT_DIR/dmg-stage"
DMG_PATH="$OUT_DIR/${APP_NAME}-${VERSION}-${RID}.dmg"

EXECUTABLE_NAME="Rejector.Avalonia"
ICON_SOURCE_PNG="src/Icon/6dd39f16-9f8b-454e-86c4-b44c409cb647.png"
ICONSET_DIR="$OUT_DIR/AppIcon.iconset"
ICNS_PATH="$OUT_DIR/AppIcon.icns"
if [[ ! -f "$PROJECT" ]]; then
  echo "Project not found: $PROJECT"
  exit 1
fi

echo "[1/4] Publishing $PROJECT for $RID..."
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained "$SELF_CONTAINED" \
  -o "$PUBLISH_DIR" \
  /p:Version="$VERSION" \
  /p:AssemblyVersion="$VERSION" \
  /p:FileVersion="$VERSION" \
  /p:InformationalVersion="v$VERSION"

echo "[2/4] Building .app bundle..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR"/. "$APP_DIR/Contents/MacOS/"

if [[ -f "$ICON_SOURCE_PNG" ]]; then
  echo "Generating app icon..."
  rm -rf "$ICONSET_DIR"
  mkdir -p "$ICONSET_DIR"
  sips -z 16 16     "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
  sips -z 32 32     "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
  sips -z 32 32     "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
  sips -z 64 64     "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
  sips -z 128 128   "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
  sips -z 256 256   "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
  sips -z 256 256   "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
  sips -z 512 512   "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
  sips -z 512 512   "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$ICON_SOURCE_PNG" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null
  iconutil -c icns "$ICONSET_DIR" -o "$ICNS_PATH"
  cp "$ICNS_PATH" "$APP_DIR/Contents/Resources/AppIcon.icns"
else
  echo "Warning: icon source not found at $ICON_SOURCE_PNG, app bundle will have no icon"
fi

cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key><string>$EXECUTABLE_NAME</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
  </dict>
</plist>
EOF

if [[ ! -f "$APP_DIR/Contents/MacOS/$EXECUTABLE_NAME" ]]; then
  echo "Expected executable not found: $APP_DIR/Contents/MacOS/$EXECUTABLE_NAME"
  exit 1
fi
chmod +x "$APP_DIR/Contents/MacOS/$EXECUTABLE_NAME"

echo "[3/4] Preparing DMG staging..."
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP_DIR" "$DMG_STAGE/"
cp "$DMG_README" "$DMG_STAGE/README.txt"
ln -sfn /Applications "$DMG_STAGE/Applications"

echo "[4/4] Creating DMG..."
rm -f "$DMG_PATH"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$DMG_STAGE" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

echo "DMG created: $DMG_PATH"
echo "Done."
