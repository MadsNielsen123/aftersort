# aftersort
Image Sorting

## Download

Prebuilt, self-contained builds (no .NET install needed) are on the
[releases page](https://github.com/MadsNielsen123/aftersort/releases):

- **Latest build (master)** — rebuilt automatically on every push. Always current,
  but unreviewed.
- **Tagged releases** (`v0.1.0`, ...) — fixed snapshots that never change.

Each one attaches all four platforms:

| Platform | File |
|---|---|
| Windows x64 | `aftersort-<version>-win-x64.zip` |
| Linux x64 | `aftersort-<version>-linux-x64.tar.gz` |
| macOS Intel | `aftersort-<version>-osx-x64.zip` |
| macOS Apple Silicon | `aftersort-<version>-osx-arm64.zip` |

Unzip and run `AfterSort` (`AfterSort.exe` on Windows, `AfterSort.app` on macOS).

The builds are unsigned. macOS blocks them until you clear the quarantine flag:

```sh
xattr -dr com.apple.quarantine /Applications/AfterSort.app
```

**Linux video support** needs system VLC — `sudo apt install vlc` (or your
distro's equivalent). Without it images still work; videos fall back to
placeholder thumbnails. Windows and macOS builds bundle their own libvlc.

## Releasing

Pushing to `master` refreshes the rolling `latest` prerelease automatically —
nothing to do.

To cut a stable release, tag the commit:

```sh
git tag v0.1.0
git push origin v0.1.0
```

The `Release` workflow builds all four targets and publishes them under that tag.
