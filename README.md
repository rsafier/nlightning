# NLightning - A C# dotNet Lightning Implementation

[![CI Build](https://github.com/ngoline/nlightning/actions/workflows/dotnet.yml/badge.svg)](https://github.com/ngoline/nlightning/actions/workflows/dotnet.yml)
&nbsp;
[![GitHub](https://img.shields.io/badge/GitHub-ngoline/nlightning-informational?style=flat&logo=github)](https://github.com/ngoline/nlightning)
&nbsp;
[![MIT License](https://img.shields.io/github/license/ngoline/nlightning)](LICENSE)
&nbsp;
![.NET 10.0 | 11.0](https://img.shields.io/badge/Version-.NET%2010.0%20%7C%2011.0-informational?style=flat&logo=dotnet)

Welcome to the C# implementation of the Lightning Network!

This project aims to provide a robust and efficient implementation of the Lightning Network protocol in C#.
We adhere to the Basis of Lightning Technology (BOLT) specifications to ensure compatibility with other
Lightning Network implementations.

<img src="images/logo.png" alt="NLightning Logo"> 

## Documentation

You can check our documentation page [here](https://docs.nlightn.ing/)

## Features

- **BOLT Compatibility:** We follow the Basis of Lightning Technology (BOLT) specifications to maintain compatibility
  with other Lightning Network implementations.
- **Modular Design:** The implementation is designed with modularity in mind, making it easy to extend and customize.
- **Efficiency:** We strive for efficient and optimized code to ensure fast and reliable performance.
- **Community Support:** We welcome contributions and feedback from the community to improve and enhance the project.

## Current State of BOLT implementation

| BOLT                                                      | Library (API) | Full Node (daemon) | Notes                                                                                   |
|-----------------------------------------------------------|:-------------:|:------------------:|-----------------------------------------------------------------------------------------|
| BOLT 1: Base Protocol                                     |       ✅       |         ✅          | init, ping/pong keep-alive, error/warning, peer storage                                 |
| BOLT 2: Peer Protocol for Channel Management              |       ✅       |         ✅          | v1 and dual-funded opens (with RBF), HTLCs, reestablish, cooperative and simple close    |
| BOLT 3: Bitcoin Transaction and Script Formats            |       ✅       |         ✅          | static_remotekey and anchors; every spec vector byte-exact                              |
| BOLT 4: Onion Routing Protocol                            |       ✅       |         ✅          | forwarding, MPP, route blinding, onion messages, attribution data, keysend              |
| BOLT 5: Recommendations for On-chain Transaction Handling |       ✅       |         ✅          | force close, HTLC claims, penalties, anchors CPFP, RBF sweeps, reorgs                   |
| BOLT 7: P2P Node and Channel Discovery                    |       ✅       |         ✅          | public channels, graph sync and relay, pathfinding; `option_scid_alias` off by default  |
| BOLT 8: Encrypted and Authenticated Transport             |       ✅       |         ✅          | TCP and Tor (v3 onion service)                                                          |
| BOLT 9: Assigned Feature Flags                            |       ✅       |         ✅          |                                                                                         |
| BOLT 10: DNS Bootstrap and Assisted Node Location         |       ✅       |         ✅          | DNS seed client, on by default on mainnet                                               |
| BOLT 11: Invoice Protocol for Lightning Payments          |       ✅       |         ✅          | route hints, bLIP 39 blinded paths                                                      |
| BOLT 12: Offers                                           |       ✅       |         ✅          | offers, invoice requests and invoices over onion messages, both directions              |

Protocol extensions:

| Extension                                                 | Library (API) | Full Node (daemon) | Notes                                                                                   |
|-----------------------------------------------------------|:-------------:|:------------------:|-----------------------------------------------------------------------------------------|
| Splicing, quiescence (`option_splice`, `option_quiesce`)  |       ✅       |         ✅          | splice in/out and splice RBF                                                            |
| Liquidity ads (BOLTs PR #1153)                            |       ✅       |         ✅          | buy and sell inbound liquidity; buying proven against Eclair, selling node-to-node only |
| Simple taproot channels (`option_simple_taproot`)         |       ✅       |         🧪          | experimental: MuSig2 channels, private only; taproot gossip not yet                    |
| Trampoline routing (BOLTs PR #836)                        |       ✅       |         🧪          | experimental: pay through and relay as a trampoline node                               |

✅ implemented · 🧪 implemented, experimental (off by default, `Features:AllowExperimentalFeatures`).
Details per requirement in [`docs/agents/BOLT_COVERAGE.md`](docs/agents/BOLT_COVERAGE.md).

## Quick Start

This section will guide you through getting a copy of NLightning up and running on your local machine for development
and testing purposes.

### Prerequisites

Before you begin, ensure you have the following installed on your system:

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later), and optionally the
  [.NET 11.0 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) (release candidates are accepted until 11.0 is GA)
- Git (for cloning the repository)

The projects target .NET 10.0 (LTS), and also .NET 11.0 when they are built with SDK 11 or newer, so SDK 10 alone is
enough. `global.json` requires SDK 10.0.100 or later and rolls forward to the newest installed SDK
(`rollForward: latestMajor`, prereleases allowed while .NET 11 is in RC). With SDK 11 installed, build, test or run a
single framework with `-f net10.0` / `-f net11.0` (or `-p:NltgTargetNet11=false` for net10.0 only); the net10.0 build
needs the .NET 10 runtime and the net11.0 build the .NET 11 runtime.

### Installation

1 - **Clone the repository**

First, clone the NLightning repository to your local machine using Git:

```sh
git clone https://github.com/ngoline/nlightning.git
cd nlightning
```

2 - **Build the project**

Navigate to the project directory and build the project using the .NET CLI to ensure all dependencies are properly
installed:

```sh
dotnet build
```

3 - **Versioning Policy**

Check the versioning policy of the project [here](VERSIONING.md)

4 - **Contribute to Development**

As NLightning is currently under active development, it may not be in a runnable state just yet. However, this opens up
a great opportunity for you to contribute. Whether it's implementing new features, fixing bugs, or improving
documentation, your contributions are invaluable to making NLightning fully operational.

To start contributing:

- Explore the [Issues](https://github.com/ngoline/nlightning/issues) section on GitHub to find out what needs to be
  worked on.
- Review our [Contributing Guidelines](CONTRIBUTING.md) for details on making contributions, such as how to create pull
  requests.
- If you have a new idea or feature you'd like to work on, don't hesitate to open a new issue to discuss it with the
  project maintainers.

We encourage you to dive into the codebase, familiarize yourself with the project structure, and see where your skills
and interests can help drive NLightning forward.

### Testing

To verify that everything is set up correctly, run the unit tests (no containers needed):

```sh
dotnet test -f net10.0 --filter 'FullyQualifiedName!~Docker&FullyQualifiedName!~SqlServer'
```

The integration suites (interop with LND, CLN, Eclair and LDK, on-chain, gossip, Postgres and more) run on a local
Kubernetes cluster through `scripts/run-cluster.sh`, one namespace per run so several suites run at once:

```sh
scripts/run-cluster.sh --matrix              # every suite (about 25 min)
scripts/run-cluster.sh --matrix lnd,taproot  # selected suites
scripts/run-cluster.sh -n 1 --suite cln      # one suite
```

Details are in [`test/CLAUDE.md`](test/CLAUDE.md) ("Cluster test harness"). The Tor suite is the only one still on
plain Docker (`scripts/run-interop.sh tor`).

#### macOS users

We recommend [OrbStack](https://orbstack.dev): it provides both the Docker engine and the Kubernetes cluster the
harness uses, shares one image store between them, and lets the Mac reach containers directly.

```sh
brew install orbstack
orb config set k8s.enable true   # or enable Kubernetes in OrbStack's settings
orb start

# Build the local images once (the harness never pulls them)
docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd
docker build -t nltg-eclair:0.14.3 test/Docker/eclair
docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server
```

`run-cluster.sh` uses the `orbstack` Kubernetes context by default.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Support

As we venture into the development of NLightning, our mission is to create a high-performance, secure, and easy-to-use
Lightning Network implementation in C#. This project not only aims to contribute to the scalability and adoption of
Bitcoin but also seeks to provide developers with a reliable toolset for building innovative applications on top of the
Lightning Network.

However, this journey is not without its challenges. Development, testing, and maintenance require significant resources
and dedicated effort. While we are passionate about pushing the boundaries of what's possible with Lightning Network
technology, we also recognize the power of community support in achieving these ambitious goals.

### Why Your Support Matters

Your donations will directly contribute to:

- Speeding up the development process by allowing us to dedicate more time to the project.
- Improving documentation and tutorials, making the technology accessible to more developers.
- Expanding our testing frameworks to ensure reliability and security.
- Supporting the infrastructure needed for development and testing.

By donating, you become an integral part of the NLightning project, helping to ensure its success and continued
advancement. Whether you're a user looking forward to a stable release, a developer eager to contribute, or simply a
supporter of open-source innovation, your contribution is immensely appreciated.

### How to Support Us

If you find this project useful, consider supporting us:

- **Bitcoin on-chain address**: `bc1pgtdj7qtdfrate2hhnt5lecayvgafhmpu6t250dg7d0sdrwtwcnkq8usux8`

No matter the size, every donation makes a difference and is deeply appreciated. Together, we can make NLightning a
cornerstone of the Lightning Network ecosystem.

Thank you for your support and belief in our project.

## Contact

If you have any questions, feedback, or suggestions, feel free to reach out to us at
[reachus@nlightn.ing](mailto:reachus@nlightn.ing).
