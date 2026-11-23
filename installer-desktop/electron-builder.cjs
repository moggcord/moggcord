/*
 * Copyright (c) 2026 Moggcord contributors
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

const signMac = process.env.MOGGCORD_MAC_SIGNING === "true";

module.exports = {
    appId: "dev.moggcord.installer",
    productName: "Moggcord Installer",
    copyright: "Copyright 2026 Moggcord contributors",
    electronVersion: "41.0.3",
    asar: true,
    npmRebuild: false,
    files: ["main.cjs", "preload.cjs", "index.html", "icon.png", "icon.icns", "LICENSE", "Inter.LICENSE.txt", "THIRD-PARTY.txt"],
    extraResources: [{ from: "resources", to: "moggcord-installer", filter: ["**/*"] }],
    directories: { output: "../../../release/platform-installers" },
    forceCodeSigning: signMac,
    mac: {
        target: [{ target: "dmg", arch: ["universal"] }],
        category: "public.app-category.utilities",
        icon: "icon.icns",
        identity: signMac ? process.env.CSC_NAME?.trim().toUpperCase() : "-",
        signIgnore: ["/equilotl$"],
        notarize: false,
        artifactName: "Moggcord-Installer-macOS.dmg"
    },
    dmg: { title: "Moggcord Installer", sign: signMac },
    linux: { target: [{ target: "AppImage", arch: ["x64"] }, { target: "deb", arch: ["x64"] }], category: "Utility", icon: "icon.png", executableName: "moggcord-installer", artifactName: "Moggcord-Installer-Linux.${ext}" },
    deb: { maintainer: "Moggcord contributors", depends: ["libgtk-3-0", "libnss3", "libasound2", "libgbm1", "libxss1", "libxtst6", "libnotify4", "libsecret-1-0", "xdg-utils", "policykit-1 | polkitd"] },
    publish: null
};
