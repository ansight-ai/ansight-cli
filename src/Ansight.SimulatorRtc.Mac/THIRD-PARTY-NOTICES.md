# Third-party notices

The native WebRTC prototype is built from exact upstream revisions by
`Native/build-native-dependencies.sh` and statically linked into the Mac Catalyst app.

| Component | Revision | License |
| --- | --- | --- |
| [libdatachannel](https://github.com/paullouisageneau/libdatachannel) | `c6696d157b5612df2a741d9a03b192b47ab6cefb` (v0.24.3) | Mozilla Public License 2.0 |
| [libjuice](https://github.com/paullouisageneau/libjuice) | revision pinned by libdatachannel v0.24.3 | Mozilla Public License 2.0 |
| [usrsctp](https://github.com/paullouisageneau/usrsctp) | revision pinned by libdatachannel v0.24.3 | BSD 3-Clause |
| [libsrtp](https://github.com/cisco/libsrtp) | revision pinned by libdatachannel v0.24.3 | BSD 3-Clause |
| [plog](https://github.com/SergiusTheBest/plog) | revision pinned by libdatachannel v0.24.3 | Mozilla Public License 2.0 |
| [Mbed TLS](https://github.com/Mbed-TLS/mbedtls) | `c765c831e5c2a0971410692f92f7a81d6ec65ec2` (3.6.4) | Apache License 2.0 |

The build enables `MBEDTLS_SSL_DTLS_SRTP` in Mbed TLS. Keep the corresponding source,
notices, and any MPL-covered modifications available when distributing the prototype.
