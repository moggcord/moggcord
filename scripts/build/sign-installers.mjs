/*
 * Copyright (c) 2026 Moggcord contributors
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

import { execFileSync } from "node:child_process";
import { constants, copyFileSync, existsSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { extname, join, resolve } from "node:path";

function run(tool, args, options = {}) {
    return execFileSync(tool, args, { encoding: "utf8", stdio: ["pipe", "pipe", "inherit"], ...options });
}

function signTool() {
    if (process.env.MOGGCORD_SIGNTOOL) return process.env.MOGGCORD_SIGNTOOL;
    const programFiles = process.env["ProgramFiles(x86)"] ?? process.env.ProgramFiles;
    if (programFiles) {
        const sdk = join(programFiles, "Windows Kits", "10", "bin");
        if (existsSync(sdk)) {
            const versions = readdirSync(sdk).filter(name => /^10\.\d+\.\d+\.\d+$/.test(name));
            versions.sort((a, b) => b.localeCompare(a, "en", { numeric: true }));
            const path = versions.map(version => join(sdk, version, "x64", "signtool.exe")).find(existsSync);
            if (path) return path;
        }
    }
    return "signtool.exe";
}

function signWindows(file, verifyOnly) {
    const thumbprint = process.env.MOGGCORD_WINDOWS_CERT_THUMBPRINT?.trim();
    if (!verifyOnly && !thumbprint) {
        process.stdout.write("Windows installer signing skipped because no certificate is configured.\n");
        return;
    }
    const tool = signTool();
    if (!verifyOnly) {
        if (!/^[\da-f]{40}$/i.test(thumbprint)) throw new Error("Set MOGGCORD_WINDOWS_CERT_THUMBPRINT to the Windows certificate thumbprint.");
        const timestamp = process.env.MOGGCORD_WINDOWS_TIMESTAMP_URL || "http://timestamp.digicert.com";
        run(tool, ["sign", "/sha1", thumbprint, "/fd", "SHA256", "/tr", timestamp, "/td", "SHA256", file], { stdio: "inherit" });
    }
    run(tool, ["verify", "/pa", "/tw", file], { stdio: "inherit" });
}

function signMacHelper(file) {
    const thumbprint = process.env.CSC_NAME?.trim().toUpperCase();
    if (!thumbprint || !/^[\da-f]{40}$/i.test(thumbprint)) throw new Error("Set CSC_NAME to the Developer ID Application certificate thumbprint.");
    const identities = run("security", ["find-identity", "-v", "-p", "codesigning"]);
    if (!identities.split("\n").some(line => line.includes(thumbprint) && line.includes('"Developer ID Application:'))) {
        throw new Error("The configured Developer ID Application identity is missing from the macOS keychain.");
    }
    run("codesign", ["--force", "--options", "runtime", "--timestamp", "--sign", thumbprint, file]);
    run("codesign", ["--verify", "--strict", file]);
}

function notarizeDmg(file) {
    const { MOGGCORD_APPLE_NOTARY_KEY, APPLE_API_KEY_ID, APPLE_API_ISSUER, ...env } = process.env;
    if (!MOGGCORD_APPLE_NOTARY_KEY || !APPLE_API_KEY_ID || !APPLE_API_ISSUER) throw new Error("Configure the Apple notarization API key, key ID and issuer.");
    const temp = mkdtempSync(join(tmpdir(), "moggcord-notary-"));
    try {
        const key = join(temp, "notary.p8");
        writeFileSync(key, MOGGCORD_APPLE_NOTARY_KEY, { mode: 0o600 });
        const result = JSON.parse(run("xcrun", ["notarytool", "submit", file, "--wait", "--output-format", "json",
            "--key", key, "--key-id", APPLE_API_KEY_ID, "--issuer", APPLE_API_ISSUER], { env }));
        if (result.status !== "Accepted") throw new Error(`Apple rejected the installer notarization request (${result.id}).`);
        run("xcrun", ["stapler", "staple", file], { env });
        run("xcrun", ["stapler", "validate", file], { env, stdio: "inherit" });
    } finally {
        rmSync(temp, { recursive: true });
    }
}

function signChecksums(file) {
    const { MOGGCORD_GPG_PRIVATE_KEY, MOGGCORD_GPG_PASSPHRASE, ...env } = process.env;
    const fingerprint = env.MOGGCORD_GPG_FINGERPRINT?.trim();
    if (!MOGGCORD_GPG_PRIVATE_KEY || !fingerprint || !/^[\da-f]{40}$/i.test(fingerprint)) {
        throw new Error("Configure the armored OpenPGP private key and its fingerprint.");
    }
    const home = mkdtempSync(join(tmpdir(), "moggcord-gpg-"));
    const gpg = env.MOGGCORD_GPG ?? "gpg";
    const options = ["--homedir", home, "--batch", "--no-tty"];
    const gpgProcess = { env, stdio: ["pipe", "ignore", "inherit"] };
    try {
        const signature = join(home, "checksums.asc");
        run(gpg, [...options, "--import"], { ...gpgProcess, input: MOGGCORD_GPG_PRIVATE_KEY });
        run(gpg, [...options, "--pinentry-mode", "loopback", "--passphrase-fd", "0",
            "--local-user", fingerprint, "--armor", "--output", signature, "--detach-sign", file],
        { ...gpgProcess, input: (MOGGCORD_GPG_PASSPHRASE ?? "") + "\n" });
        run(gpg, [...options, "--verify", signature, file], gpgProcess);
        copyFileSync(signature, file + ".asc", constants.COPYFILE_EXCL);
    } finally {
        rmSync(home, { recursive: true, force: true, maxRetries: 3 });
    }
}

const [action, artifact] = process.argv.slice(2);
const extensions = { windows: ".exe", "windows-verify": ".exe", "mac-helper": "", "mac-dmg": ".dmg", checksums: ".txt" };
if (!Object.hasOwn(extensions, action) || !artifact || extname(artifact).toLowerCase() !== extensions[action]) {
    throw new Error("Usage: node scripts/build/sign-installers.mjs <windows|windows-verify|mac-helper|mac-dmg|checksums> <file>");
}
const file = resolve(artifact);
const info = statSync(file);
if (!info.isFile() || !info.size) throw new Error("Choose a nonempty installer artifact.");

switch (action) {
    case "windows":
    case "windows-verify":
        signWindows(file, action === "windows-verify");
        break;
    case "mac-helper":
        signMacHelper(file);
        break;
    case "mac-dmg":
        notarizeDmg(file);
        break;
    case "checksums":
        signChecksums(file);
        break;
}
