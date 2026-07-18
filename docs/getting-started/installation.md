# Installation

## Standalone binary (recommended)

No runtime required. The installer downloads the right archive for your
platform, verifies its checksum, and puts `tx` on your `PATH`:

=== "Linux / macOS"

    ```sh
    curl -LsSf https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.sh | sh
    ```

=== "Windows"

    ```powershell
    powershell -ExecutionPolicy ByPass -c "irm https://raw.githubusercontent.com/bgarcevic/tomix-cli/main/install/install.ps1 | iex"
    ```

Archives for six platforms are on the
[releases page](https://github.com/bgarcevic/tomix-cli/releases), checksums
included, if you prefer to install manually.

## .NET tool

If you have the .NET SDK installed:

```sh
dotnet tool install -g Tomix.Cli
```

## From source

You need the .NET 10 SDK:

```sh
git clone https://github.com/bgarcevic/tomix-cli
cd tomix-cli
dotnet build
./tx doctor        # .\tx.ps1 doctor on Windows
```

`./tx` wraps `dotnet run` — it always reflects your current source. To install
your build as a real global tool, run `./scripts/install-dev.sh`
(`./scripts/install-dev.ps1` on Windows).

## Verify the install

```sh
tx doctor
```

`doctor` checks that your environment is ready and is the first thing to
attach to a bug report when something seems off.

!!! note "Platform support"

    Connecting to a locally running Power BI Desktop instance (`--local`) is
    Windows-only. Everything that operates on TMDL/BIM files — including the
    full test suite — works on Linux, macOS, and Windows.

## Shell completion

```sh
tx completion bash >> ~/.bashrc          # bash
tx completion zsh                        # zsh
tx completion fish                       # fish
tx completion powershell | Invoke-Expression   # PowerShell
```
