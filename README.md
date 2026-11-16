# Mintpilot for Windows

Mintpilot is a native WPF application for Solana mainnet. Download the Mintpilot-Windows-x64 artifact from a successful GitHub Actions run, extract the ZIP, and open Mintpilot.exe on Windows 10 or 11 x64. The application includes its .NET runtime. This is an unsigned development build.

## First launch

Mintpilot generates a unique Solana wallet locally and encrypts its seed and service credentials for your Windows user using DPAPI. Open Wallet to copy the real deposit address and save a password-encrypted wallet backup. A backup is needed to restore the wallet under another Windows user or on another computer.

Settings and transaction history are stored at %LOCALAPPDATA%/MintpilotNative/workspace.dat. The application stops if an existing workspace cannot be unlocked, preserving that file.

In Settings, enter an HTTPS Solana mainnet RPC URL and a Pinata JWT with permission to upload public files. Choose your initial buy, maximum total wallet debit per launch, daily UTC budget, daily launch count, slippage, priority fee, and SOL reserve. The public Solana RPC is configured initially. A dedicated RPC may be needed for provider limits.

For AI concepts, choose Ollama with a locally installed model (default llama3.2:3b on localhost port 11434), or OpenAI with your own API key and model name (for example gpt-4.1-mini). AI service access is configured on your computer.

To create a coin manually, choose Launch a coin and enter the name, ticker, description, optional promotion text, and optional PNG path. Blank image paths use a generated native token icon. Autopilot generates concepts with the configured AI and creates icons locally.

## Funding, launches, and withdrawals

Transfer SOL on Solana mainnet to the address shown in Wallet. Mintpilot requests a serialized launch transaction from PumpPortal's local transaction API, validates its fee payer, required signers, instruction types, mint, metadata and spending limit, signs on your PC, simulates through your configured RPC, and submits the transaction.

Signed transactions and their signatures are recorded before submission. Ambiguous submissions are reconciled using the original signature. The app waits for finalization and reads actual fees and wallet debits from on-chain transaction metadata. It pauses after an uncertain or failed operation instead of automatically creating a replacement.

Available SOL excludes pending transaction reservations. Withdraw SOL sends spendable SOL after the network fee to the address you enter. Leave the amount blank to withdraw the available maximum. Sell remaining tokens in Launches converts the selected coin's token holdings to SOL through PumpPortal, subject to your configured slippage. Market execution and available liquidity determine sale proceeds.

Autopilot runs while the application is open and Windows is awake. It starts paused after reopening. Closing the application stops future scheduled work. Already submitted transactions remain tracked on reopening.

The dashboard shows the wallet's on-chain SOL balance, recorded launch results and spending, network fees, sales proceeds, promotion delivery, token holdings, and available market information from DexScreener. Market values are estimates. Prices remain unavailable until a supported market is indexed. Statistics cover operations made through this app.

## Promotion

Enable Telegram with a bot token and a destination chat ID, and give that bot posting access to the destination. Enable X with your developer application's OAuth 1.0a consumer key, consumer secret, user access token, and user access token secret with Read and Write access.

Posts include the mint's pump.fun link and an automated-creator disclosure. Enabled channels publish after the launch finalizes. Posts and delivery identifiers are saved locally. A delivery with an ambiguous outcome is marked Unknown for review before a manual retry.

RPC, IPFS, AI and social APIs require internet access and may have service fees or account requirements. The app uses PumpPortal as a third-party integration. There are no service credentials, funded wallets, or private keys in this repository.

## Verification

The Windows workflow compiles the app, runs deterministic cryptographic and transaction checks, verifies encrypted wallet persistence and backups, checks pending-transaction recovery and budget enforcement, starts and renders all five native views, and packages the executable. These checks use generated wallets and a mocked chain. A funded mainnet launch and live promotion require configuration and remain separate from automated verification.

## Build from source

Install the .NET 10 SDK on Windows.

```powershell
dotnet build src/Mintpilot/Mintpilot.csproj -c Release
dotnet run --project src/Mintpilot/Mintpilot.csproj -c Release -- --self-test
dotnet publish src/Mintpilot/Mintpilot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
