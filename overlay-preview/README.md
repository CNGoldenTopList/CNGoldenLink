# Overlay 视觉预览

控制台页与两套原生 HTML/CSS/JS 主题：APEX（冰蓝转播）与 ORBIT（铜金圆弧）。独立 Node 预览使用虚构演示数据；相同前端已作为资源嵌入 Mod，由其提供真实本地数据。

需要 Node.js 20 或更新版本，无需 `npm install`。控制台图表使用随仓库提供的 `public/chart.umd.min.js`（Chart.js 4.5.1，MIT，许可证见 `licenses/`）。在此目录运行：

```powershell
npm start
```

打开 http://localhost:32271/ 查看控制台（演示图表与设置），或 http://localhost:32271/apex、/orbit 查看主题。页面下方可以推进房间、播放演示、切换带金、暂停及断线状态。

OBS 浏览器源使用 `/apex?obs=1` 或 `/orbit?obs=1`，尺寸为 1920×1080。将 1600×900 的游戏源放在其下方，位置为 X=24、Y=24。透明页隐藏预览背景和控制台，保留 DEMO 标识。当前 OBS 页同样是演示快照。

## 数据与主题

- `public/index.html`、`hub.css`、`hub.mjs`：控制台（OBS 链接、挑战选择、图表、设置）。
- `public/insights.mjs`：控制台数据白名单转换（`goldenlink.insights/1` 与设置），可单独测试。
- `public/overlay.html`：两套主题共享的 1920×1080 页面结构。
- `public/style.css`：主题与动画，不依赖外部字体或图片。
- `public/app.mjs`：展示逻辑与预览交互。
- `public/data.mjs`：独立 HTTP 适配器及白名单数据转换，缺失数值保留为 null。
- `public/demo.mjs`：独立的虚构数据样本，示范 `goldenlink.overlay/1` 与 `goldenlink.insights/1` 数据格式。
- `server.mjs`：仅监听本机的开发预览服务器，不打包进 Mod。

Mod 托管 `public` 静态资源，并将 CCT 与 CNGist 数据组合为同一展示格式，通过本地 `/api/overlay/state` 返回。此路径已在 Mod 中实现；Node 预览服务器不提供真实数据。使用 `?source=live`（OBS 则为 `?obs=1&source=live`）可切换到真实 HTTP 适配器，每 500ms 读取一次；失败时显示断线或等待状态，不回退到演示数据。这个本地读取间隔与 Mod 向远端的上传（按房间变化触发）互相独立。

界面不接触 OAuth 凭据；认证与金榜资料匹配应由 Mod 完成。带金概率沿用 CNGist 的路线投影语义；历史样本取当前房间最近 20 次，CP 列表跟随当前 CP 展示附近三个节点。

运行数据契约检查：`npm test`。
