# Third-party notices

The native WebRTC prototype is built from exact upstream revisions by
`Native/build-native-dependencies.sh` and statically linked into the Mac Catalyst app.

| Component | Revision | License |
| --- | --- | --- |
| [libdatachannel](https://github.com/paullouisageneau/libdatachannel) | `443f6934d9007eb7076ab7825ba330f355fcbead` (v0.24.5) | Mozilla Public License 2.0 |
| [libjuice](https://github.com/paullouisageneau/libjuice) | revision pinned by libdatachannel v0.24.5 | Mozilla Public License 2.0 |
| [usrsctp](https://github.com/paullouisageneau/usrsctp) | revision pinned by libdatachannel v0.24.5 | BSD 3-Clause |
| [libsrtp](https://github.com/cisco/libsrtp) | revision pinned by libdatachannel v0.24.5 | BSD 3-Clause |
| [plog](https://github.com/SergiusTheBest/plog) | revision pinned by libdatachannel v0.24.5 | Mozilla Public License 2.0 |
| [Mbed TLS](https://github.com/Mbed-TLS/mbedtls) | `068ff080b369adfac81509f9b57b2afabaf82dc5` (3.6.7) | Apache License 2.0 |

The build enables `MBEDTLS_SSL_DTLS_SRTP` in Mbed TLS. Keep the corresponding source,
notices, and any MPL-covered modifications available when distributing the prototype.
