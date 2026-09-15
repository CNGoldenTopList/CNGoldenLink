# CNGoldenLink

CN 金榜的 Celeste 联动 Mod，版本 0.2.3。

在 Mod 菜单开启连接后，通过浏览器登录 CN 金榜并授权，默认每 5 秒同步当前状态、CCT 统计和地图死亡数据。同步间隔可在菜单调整，服务地址可通过设置文件的 `ServiceBaseUrl` 修改。

无金完整通关最少死亡始终在本地记录；连接开关只控制上传，重新连接后补传已保存的纪录。当前支持 Windows，依赖版本见 `everest.yaml`。

同步本体存档当前地图面的 `Completed` 标记（已通关/未通关/未知），进入地图时读取已有通关记录，通关事件立即采样；与 PB 一同缓存并在重连后补传。实时状态携带 `datasetId`，避免服务端混用不同存档的通关记录。

连接开启时逐帧检测地图、房间和持金状态变化，在原上传周期内通过 `presence.transitions` 顺序批量发送，因此快速经过目标房间再离开或死亡仍能触发 QQ 带金播报。批次收到 ACK 后才释放，响应丢失时按原 sequence 和内容重试。待发队列最多 256 条，只保留最近 60 秒；在途批次最多重试 60 秒，超限显示数据错误。队列在内存中，退出游戏不持久化历史播报。需先部署服务端 `0033_presence_batch_ack` 迁移及批量接口，再更新 Mod。

## OBS Overlay

自 0.2.0 起内置 OBS Overlay。提供 APEX（冰蓝转播）和 ORBIT（铜金圆弧）两套主题，由 Mod 在本机托管，页面和视觉资源随安装包提供，运行时无需 Node.js。

### 接入 OBS

1. 在 Mod 菜单开启 `OBS Overlay`，点击 `Open Overlay / 打开控制页`。默认控制页为 `http://localhost:32272/apex`，ORBIT 主题使用 `http://localhost:32272/orbit`。
2. 在 OBS 添加浏览器源，URL 填写 `http://localhost:32272/apex?obs=1` 或 `http://localhost:32272/orbit?obs=1`，宽度设为 **1920**、高度设为 **1080**。透明页隐藏控制界面，为游戏画面留出透明区域。
3. 将游戏源放在浏览器源下方，位置设为 **X=24、Y=24**，尺寸设为 **1600×900**。
4. 进入地图后，在普通浏览器的控制页选择当前挑战，选择会自动保存并同步到 OBS；只有一个挑战时自动选中。

Overlay 开关独立于金榜上传开关，关闭上传仍可显示本地 CCT 数据。金榜地图、挑战和 Tier 需要先通过现有连接流程完成授权，并由服务端提供 `/api/tracker/overlay-context` 接口。

### 显示内容与数据

- 当前地图、地图包、所选挑战及金榜 Tier，以及练习、带金、暂停和断线状态。
- 当前房间、路线进度、当前 CP 附近的三个节点、连续通过次数及最佳纪录、当前房间最近 20 次尝试结果。
- 当前房间的带金成功率和通过／尝试次数、累计及本次进入率、累计及本次带金死亡次数。
- 无金完整通关最少死亡纪录和地图累计死亡次数。

带金成功率和进入率按 CCT 路线、分房间带金死亡及带金通关次数推算；合并房间合并统计，重复节点只计首次，忽略房间不参与路线推算。缺少路线或有效样本的指标显示 `—`。

本地快照和页面读取每 500ms 刷新，与远端上传间隔独立。金榜资料在切图时查询，成功后每 5 分钟刷新，失败后每 30 秒重试；已有资料时暂用缓存。未授权、地图未配对或金榜服务不可用时仍可显示本地统计，不会编造挑战或 Tier。未配对地图可到金榜账户页申请配对。本地接口断线时保留最后快照并标记断线，不回退为演示数据。

### 设置与预览

服务仅监听本机，默认端口为 `32272`，可在 Mod 设置文件中通过 `OverlayPort` 修改（有效范围 `1024–65535`）。端口被占用时服务无法启动，修改后重新关闭、开启 Overlay，并同步修改浏览器和 OBS 中的 URL。挑战选择按服务地址和地图 ID 保存在游戏目录的 `CNGoldenLinkData/overlay-selections.json`。

另提供[独立视觉预览及前端说明](overlay-preview/README.md)：使用 Node.js 20 或更新版本，在 `overlay-preview` 目录运行 `npm start`，访问 `http://localhost:32271/apex` 或 `http://localhost:32271/orbit`。此预览使用虚构演示数据；OBS 接入真实游戏数据时使用上述 Mod 的 `32272` 端口。

## 构建

需要 Windows、Python 3、.NET 8 SDK、安装 Everest 的 Celeste，以及配置版本的 CCT。先启动一次游戏，生成 CCT 程序集缓存。

默认将项目放在 Celeste 游戏目录下，在项目目录执行：

```powershell
dotnet build -c Release
./scripts/package.ps1
```

其他目录通过参数指定游戏路径：

```powershell
./scripts/package.ps1 -CelestePath "E:/SteamLibrary/steamapps/common/Celeste"
```

生成的 `artifacts/CNGoldenLink-0.2.3.zip` 放入游戏 `Mods` 目录，移走旧版本后启动游戏。安装包不包含游戏或 CCT 程序集。

打包脚本自动扫描 `Dialog/Simplified Chinese.txt`，对照原版字库生成缺字补充，将 `.fnt` 和 PNG 放入安装包的 `Dialog/Fonts`。模组自身的中文 Dialog 无需额外安装 Chinese Font Pack 或 Extended Chinese Fonts。每次打包都会重新生成，新增文案无需手动补字；运行时返回的任意中文文本不在此覆盖范围，OBS Overlay 使用浏览器字体。

字体构建输入固定在 `tools/fonts`，生成前校验 SHA-256，无需下载或安装系统字体。可单独执行 `python scripts/generate-fonts.py`，结果位于 `artifacts/fonts`。字体工具和原始 OTF 不进入发布包，包内保留字体许可。输入来源见 [tools/fonts/README.md](tools/fonts/README.md)。

运行测试：

```powershell
dotnet run --project tests/Diagnostics.Tests.csproj -c Release
dotnet run --project sync-tests/Sync.Tests.csproj -c Release
dotnet run --project overlay-tests/OverlayTests.csproj -c Release
node --test overlay-preview/test/data.test.mjs
```

## 自动发布

GitHub Actions 在推送版本标签时运行测试、构建并发布 Release。标签必须指向 main 中的提交，格式为 `v0.2.3` 或 `0.2.3`，并与 `.csproj` 和 `everest.yaml` 中的 Mod 版本一致。Actions 页面也可手动运行构建，仅生成下载产物，不发布。

构建时自动按 `everest.yaml` 的依赖版本下载 Everest 官方 stable Release 的 `lib-stripped.zip` 和 CCT 官方对应 Release 的安装包。更新 CCT 时修改清单中的依赖版本即可；不自动追踪 latest。新版本 API 不兼容会导致编译失败，行为兼容性仍需游戏内验证。

发布新版：修改项目和清单中的 Mod 版本，提交到 main，再推送对应标签。ZIP 文件名和程序集版本自动跟随项目版本，发布包不包含下载的依赖 DLL。已存在的 Release 不会被覆盖。

无需安装游戏的本地构建（需要 Python 3）：

```powershell
python scripts/prepare-ci.py
./scripts/package.ps1 -CelestePath ./artifacts/ci/references -CctAssemblyPath ./artifacts/ci/references/ConsistencyTracker.dll
```
