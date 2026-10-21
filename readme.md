!!!EN:-----------------------------------------------------------------------------------------------------------------
> ⚠️ This repository is an **unofficial fork** with an optional direct-IP mode. Not affiliated with,
> endorsed by, or supported by the upstream author. Upstream: [yury-sch/FsCopilot](https://github.com/yury-sch/FsCopilot).

## Direct-IP build (this fork)
The original app always connects through a public rendezvous server and falls
back to an overseas relay when NAT hole punching fails — which is the case for many players behind
symmetric NAT (NAT4). This fork adds an **opt-in** mode that skips all of that and connects straight
to your peer's LAN / VPN IP (n2n, ZeroTier, Radmin, Tailscale, WireGuard, …).
**Download (Windows x64):** [latest release](https://github.com/GrainLin-0507/FsCopilot-IP-direct-connect/releases)
### Quick start
1. Download `FsCopilot-v*-direct-win-x64.zip` from Releases and extract it anywhere.
2. Run `FsCopilot.exe`. The first run asks for administrator once (it adds a UDP firewall rule);
   if it shows `Setup required`, click **Install** to download the aircraft profiles (internet needed).
3. (1)Two players: the copilot types the captain's VPN/LAN IP in the Join box — **no port needed**,
      `38510` is automatic.
   (2)Three or more players: each copilot only needs to type the captain's IP once. The clients exchange
      addresses automatically and fill in the remaining links, so everybody ends up connected to everybody

 ### What this fork changes
| Change | File |
|---|---|
| Server-less direct transport + automatic mesh | `FsCopilot/Network/DirectNetwork.cs` |
| Optional config (`fscopilot.config.json`) | `FsCopilot/NetworkConfig.cs` |
| One-shot firewall rule (UAC once, direct mode only) | `FsCopilot/Firewall.cs` |
| Wiring / UI | `FsCopilot/Program.cs`, `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml` |
| Bridge version check compares major.minor | `Connection/SimClient.cs` |
**Default behaviour is unchanged.** Without `fscopilot.config.json` (or with `"direct": false`) the
app starts in the original rendezvous/relay mode and behaves exactly like upstream.

### Support this fork
If this saved you a flight, you can buy me a coffee:

- PayPal: [paypal.me/Grainlin](https://paypal.me/Grainlin)
- WECHAT and ALI PAY:PLEASE KEEP SCROLLING DOWN

Bug reports: open an issue on this fork. Upstream support for the original app: their Discord./Or you can contact me:graingrl@outlook.com


!!!ZH_CN:------------------------------------------------------------------------------------------------------------------------------
> **这是一个非官方分支**，额外提供了可选的「直接填 IP」模式。
> 与原作者无隶属关系，也未获其背书。上游项目：
> [yury-sch/FsCopilot](https://github.com/yury-sch/FsCopilot)。MIT 授权，原作者版权声明保留在 [LICENSE](LICENSE)。

## 直连版（本分支）
原版始终要通过作者的公共会合服务器（`p2p.fscopilot.com`）牵线，打洞失败再退回海外中继——
很多在对称型 NAT（NAT4）后面的玩家根本打不通，流量只能绕道国外。本分支增加了一个**可选**的
直连模式：完全跳过会合与中继，直接连对方在局域网／虚拟局域网（n2n、ZeroTier、Radmin、
Tailscale、WireGuard……）里的 IP。下载：[latest release](https://github.com/GrainLin-0507/FsCopilot-IP-direct-connect/releases)

### 快速上手
1. 到 Releases 下载 `FsCopilot-v*-direct-win-x64.zip`，解压到任意目录。
2. 双击 `FsCopilot.exe`：首次运行会请求一次管理员权限（自动添加 UDP 防火墙规则）；
   如果提示 `Setup required`，点 **Install** 让程序自己下载机模配置（需要联网）。
3. 两个人联机：让副驾在 Join 框里填机长的虚拟局域网 IP 就行，**不用写端口**，默认就是 `38510`。
   三个人以上：每位副驾同样只填机长的 IP，**每人连一次就够了**——客户端之间会自动交换地址、把剩下的连接补齐，最终组成全网状（N 个人就是 N(N-1)/2 条连接）。

### 本分支改了什么
| 改动 | 文件 |
|---|---|
| 无服务器直连传输 + 自动组网 | `FsCopilot/Network/DirectNetwork.cs` |
| 可选配置文件（`fscopilot.config.json`） | `FsCopilot/NetworkConfig.cs` |
| 一次性防火墙规则（只弹一次 UAC，仅直连模式） | `FsCopilot/Firewall.cs` |
| 接线与界面 | `FsCopilot/Program.cs`、`ViewModels/MainViewModel.cs`、`Views/MainWindow.axaml` |
| Bridge 版本改为比较 major.minor | `Connection/SimClient.cs` |

**默认行为没有变化。** 没有 `fscopilot.config.json`（或把它设成 `"direct": false`）时，
程序仍然走原来的会合／中继模式，行为与上游完全一致。

### 支持本分支
如果这个版本帮你省了一次绕地球的延迟，可以请作者喝杯咖啡：

- PayPal：[paypal.me/Grainlin](https://paypal.me/Grainlin)
- 微信/支付宝：
- ![image text](https://github.com/GrainLin-0507/FsCopilot-IP-direct-connect/blob/main/.github/wechat%20and%20alipay%20support.png)

问题反馈请在本分支提 issue；原版应用本身的问题请找作者的 Discord。我的邮箱：graingrl@outlook.com

!!!ORIGINAL:
[![Discord](https://img.shields.io/discord/1454265644416765974?label=Discord&logo=discord&color=5865F2)](https://discord.gg/HyKMRp47ka)
[![Support on Boosty](https://img.shields.io/badge/Support-Boosty-orange)](https://boosty.to/fscopilot)

☝️ Join our Discord
☝️ Support here

> 🌐 Visit the official website — [FS Copilot — Shared Cockpit for MSFS 2024](https://fscopilot.com)

# 🛫 FS Copilot

**FS Copilot** is a companion app for **Microsoft Flight Simulator 2024** that lets multiple pilots control the same aircraft together — in real time.
Fly as a real crew. 👨‍✈️👩‍✈️

![FS Copilot](https://raw.githubusercontent.com/yury-sch/FsCopilot/refs/heads/main/preview.png)

## 💡 How It Works

FS Copilot synchronizes the aircraft position, control surfaces, and cockpit switches between both pilots.
So when one pilot turns right, the other pilot’s aircraft turns right as well. It’s like you’re both flying the same plane together.

## 🚀 Quick start
1. Launch **FS Copilot**.
   On the first run, it copies the required files into your *Community* folder automatically.
2. Start Microsoft Flight Simulator and choose a supported aircraft.
3. Enter your partner’s session code and select Join.
4. Enjoy shared flight 🛫

## ✈️ Which aircraft are supported?
You can find the list of supported aircraft directly on https://fscopilot.com/.

The list is always up to date and updates automatically.

Additionally, join to the ⁠[Discord](https://discord.gg/HyKMRp47ka) server.
Community members often share new or custom profiles there, so your aircraft might already be supported even if it’s not included by default. 

## ⚙️ Compatibility

FS Copilot is built for **Microsoft Flight Simulator 2024** but aims to remain compatible with MSFS 2020.

However, development is primarily focused on MSFS 2024, so some features may not behave exactly as intended in the 2020 version.

## 🧩 For Developers

FS Copilot is built with **.NET 9 (C#)** and designed for modular extensibility.
It includes a flexible networking layer using **peer-to-peer UDP connection by hole punching**,
allowing direct low-latency connections without external servers.

Each aircraft is defined via YAML templates that describe variable mappings,
event bindings, and transformation logic.
These templates support embedded **JavaScript expressions** for dynamic data handling
— enabling complex synchronization behavior right inside the config.

For example:

```yaml
- get: L:AS1000_PFD_SelectedNavIndex # NAV 1/2
  set: (>B:AS1000_PFD_1_NAV_Khz_Button_Push)
- get: L:PFD_CDI_Source # CDI
  set: "value < 3 ? `${value} (>K:AP_NAV_SELECT_SET)` : '(>K:TOGGLE_GPS_DRIVES_NAV1)'"
  skp: H:AS1000_PFD_SOFTKEYS_6
- get: A:KOHLSMAN SETTING MB:0, Millibars # BARO
  set: "`${value * 16} 0 (>K:KOHLSMAN_SET)`"
- get: Z:AUDIO_Knob_Selector_1 # MIC
  set: |
    switch (value) {
        case   0: return '(>H:KMA28_TRANSMISSION_KNOB_COM3)'
        case  20: return '(>H:KMA28_TRANSMISSION_KNOB_COM2)'
        case  40: return '(>H:KMA28_TRANSMISSION_KNOB_COM1)'
        case  60: return '(>H:KMA28_TRANSMISSION_KNOB_COM1_2)'
        case  80: return '(>H:KMA28_TRANSMISSION_KNOB_COM2_1)'
        case 100: return '(>H:KMA28_TRANSMISSION_KNOB_TEL)'
        default: return ''
    }
```

You can find a detailed documentation here:
👉 [FS Copilot — Definitions Guide](https://github.com/yury-sch/FsCopilot/wiki)

## 🤝 Acknowledgements

This project was inspired by the ideas explored in [YourControls](https://github.com/Sequal32/yourcontrols).

Several good concepts and approaches originated there and helped shape the early direction of this work.

*The core of this project has been written entirely from scratch*, features a distinct architecture, and is implemented in a different programming language.

## 🧑‍💻 Author

**FS Copilot** is created by aviation and MSFS enthusiast **Yury Sсherbakov.**
Born from the idea that flying together should be as easy as sitting next to your co-pilot.

> “Fly together. Control together.” ✈️
