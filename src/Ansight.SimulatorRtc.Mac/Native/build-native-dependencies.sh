#!/bin/zsh
set -euo pipefail

if (( $# != 2 )); then
  print -u2 "Usage: build-native-dependencies.sh <output-directory> <target-triple>"
  exit 64
fi

rtc_output_root=$1
rtc_target_triple=$2
case "$rtc_target_triple" in
  arm64-apple-macos12.0|x86_64-apple-macos12.0) ;;
  *)
    print -u2 "Unsupported Apple target triple: $rtc_target_triple"
    exit 64
    ;;
esac
rtc_source_root="$rtc_output_root/src"
rtc_libdatachannel_source="$rtc_source_root/libdatachannel"
rtc_mbedtls_source="$rtc_source_root/mbedtls"
rtc_libdatachannel_commit="c6696d157b5612df2a741d9a03b192b47ab6cefb"
rtc_mbedtls_commit="c765c831e5c2a0971410692f92f7a81d6ec65ec2"
rtc_sdk_path=$(/usr/bin/xcrun --sdk macosx --show-sdk-path)
rtc_cmake=$(command -v cmake)

"$rtc_cmake" -E make_directory "$rtc_source_root"

if [[ ! -d "$rtc_libdatachannel_source/.git" ]]; then
  /usr/bin/git clone --depth 1 --branch v0.24.3 \
    https://github.com/paullouisageneau/libdatachannel.git \
    "$rtc_libdatachannel_source"
fi

if [[ $(/usr/bin/git -C "$rtc_libdatachannel_source" rev-parse HEAD) != "$rtc_libdatachannel_commit" ]]; then
  print -u2 "Unexpected libdatachannel revision. Delete $rtc_libdatachannel_source and rebuild."
  exit 65
fi

/usr/bin/git -C "$rtc_libdatachannel_source" submodule update --init --recursive --depth 1

if [[ ! -d "$rtc_mbedtls_source/.git" ]]; then
  /usr/bin/git clone --depth 1 --branch mbedtls-3.6.4 \
    https://github.com/Mbed-TLS/mbedtls.git \
    "$rtc_mbedtls_source"
fi

if [[ $(/usr/bin/git -C "$rtc_mbedtls_source" rev-parse HEAD) != "$rtc_mbedtls_commit" ]]; then
  print -u2 "Unexpected Mbed TLS revision. Delete $rtc_mbedtls_source and rebuild."
  exit 65
fi

/usr/bin/git -C "$rtc_mbedtls_source" submodule update --init --recursive --depth 1
/usr/bin/python3 "$rtc_mbedtls_source/scripts/config.py" set MBEDTLS_SSL_DTLS_SRTP

"$rtc_cmake" \
  -S "$rtc_mbedtls_source" \
  -B "$rtc_output_root/mbedtls-build" \
  -G "Unix Makefiles" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$rtc_output_root/mbedtls-install" \
  -DCMAKE_C_COMPILER=/usr/bin/clang \
  -DCMAKE_C_COMPILER_TARGET="$rtc_target_triple" \
  -DCMAKE_OSX_SYSROOT="$rtc_sdk_path" \
  -DENABLE_PROGRAMS=OFF \
  -DENABLE_TESTING=OFF \
  -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
  -DUSE_STATIC_MBEDTLS_LIBRARY=ON

"$rtc_cmake" --build "$rtc_output_root/mbedtls-build" --parallel 8
"$rtc_cmake" --install "$rtc_output_root/mbedtls-build"

"$rtc_cmake" \
  -S "$rtc_libdatachannel_source" \
  -B "$rtc_output_root/libdatachannel-build" \
  -G "Unix Makefiles" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$rtc_output_root/libdatachannel-install" \
  -DCMAKE_C_COMPILER=/usr/bin/clang \
  -DCMAKE_CXX_COMPILER=/usr/bin/clang++ \
  -DCMAKE_C_COMPILER_TARGET="$rtc_target_triple" \
  -DCMAKE_CXX_COMPILER_TARGET="$rtc_target_triple" \
  -DCMAKE_OSX_SYSROOT="$rtc_sdk_path" \
  -DCMAKE_PREFIX_PATH="$rtc_output_root/mbedtls-install" \
  -DBUILD_SHARED_LIBS=OFF \
  -DUSE_MBEDTLS=ON \
  -DNO_WEBSOCKET=ON \
  -DNO_EXAMPLES=ON \
  -DNO_TESTS=ON \
  -DWARNINGS_AS_ERRORS=OFF

"$rtc_cmake" --build "$rtc_output_root/libdatachannel-build" --parallel 8
"$rtc_cmake" --install "$rtc_output_root/libdatachannel-build"
