# X-Plane native traffic plugin

The unified CAN Pilot client reuses the `XPC_for_CAN` native plugin from:

- Source: https://github.com/JianyueLab-Org/can-xplane-client
- Source revision: `815c842c792f6bbb3eea23457c472dc2aaa27f41`
- Binary package: release `v0.0.1`, `xpc-for-can-0.0.1-win-x64.zip`
- Source release SHA256: `ebed57dc965b5e3e74de1946313066022022cc5d0d6732d9373eeb1345ff56bc`
- Protocol: local UDP 49900, JSON/base64 v3

Corresponding source and build instructions:
https://github.com/JianyueLab-Org/can-xplane-client/tree/815c842c792f6bbb3eea23457c472dc2aaa27f41/native_plugin

The plugin includes these third-party components, with their original notices retained in source:

- `native_plugin/src`: adapted from swift xswiftbus, GPL-3.0-or-later.
  Copyright (C) 2013 swift Project Community / Contributors; Copyright (C) 2026 XPC for CAN contributors.
- `native_plugin/xplanemp2`: swift-project/xplanemp2, MIT.
- `native_plugin/third_party/nlohmann`: nlohmann/json, MIT.

Plugin installation and CSL rendering remain in the separate X-Plane process. The desktop client uses its shared C# FSD and voice implementations and does not include the Python voice worker.

To reproduce the plugin-only archive, run `python tools/package-xplane-plugin.py`. This is a build-time packaging helper; Python is not needed to run the client.
