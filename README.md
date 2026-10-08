# CtYun 云电脑保活

CtYun 用于登录天翼云电脑并维持 WebSocket 保活连接。当前版本支持：

- 多账号同时登录和保活。
- Windows/本机可执行文件：支持本地配置文件模式和交互输入模式。
- Docker：支持挂载本地配置文件模式和 `-it` 交互输入模式。
- 首次绑定设备需要短信验证码时，通过终端交互输入。

## 配置文件模式

程序默认在数据目录读取 `accounts.json`：

- 本机可执行文件：默认数据目录为程序所在目录。
- Docker：默认数据目录为 `/app/data`，建议挂载到宿主机。
- 也可以通过环境变量 `CTYUN_CONFIG` 指定配置文件路径。
- 也可以通过环境变量 `CTYUN_DATA_DIR` 指定数据目录。

`accounts.json` 示例：

```json
{
  "keepAliveSeconds": 60,
  "restartIntervalMinutes": 30,
  "restartJitterSeconds": 60,
  "sessionCooldownSeconds": 10,
  "accounts": [
    {
      "name": "account-a",
      "user": "你的账号1",
      "password": "你的密码1",
      "deviceCode": "web_自行生成的32位随机字符"
    },
    {
      "name": "account-b",
      "user": "你的账号2",
      "password": "你的密码2"
    }
  ]
}
```

字段说明：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `keepAliveSeconds` | 60 | 单个 WebSocket 保活周期的长度，到期强制重连。 |
| `restartIntervalMinutes` | 30 | **定时重启保活任务的周期**。到期后重新登录、重新获取连接票据并重建保活会话。设为 `0` 关闭轮换（等同旧版本行为）。 |
| `restartJitterSeconds` | 60 | 会话时长的随机抖动上限，用于多账号错峰，避免同时重新登录。 |
| `sessionCooldownSeconds` | 10 | 两段会话之间的冷却时间，给服务端释放旧会话的时间。 |
| `accounts[].deviceCode` | 自动生成 | 设备码，留空时自动生成并保存到 `devices/{账号名}.txt`。 |

也可以用环境变量 `CTYUN_RESTART_INTERVAL_MINUTES` 覆盖 `restartIntervalMinutes`，便于在不改配置文件的情况下调整轮换周期。

`deviceCode` 可不填。程序会为每个账号自动生成设备码，并保存到 `devices/{账号名}.txt`。为了避免每次 Docker 重建镜像后重新绑定设备，务必持久化数据目录。

Linux 生成设备码示例：

```bash
echo "web_$(cat /dev/urandom | tr -dc 'a-zA-Z0-9' | fold -w 32 | head -n 1)"
```

## 本机运行

把 `accounts.json` 放到程序目录后直接运行：

```bash
CtYun.exe
```

如果没有配置文件，也没有环境变量，程序会进入交互输入模式：

```text
账号:
密码:
继续添加账号? (y/N):
```

首次设备绑定时，程序会提示输入短信验证码。

## Docker 首次运行

准备宿主机配置目录：

```bash
mkdir -p ./ctyun-data
```

把 `accounts.json` 放到 `./ctyun-data/accounts.json`。首次运行建议使用 `-it`，方便输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  ghcr.io/ksamni/ctyun:latest
```

看到保活任务启动后，说明设备码已经绑定成功。之后可以按 `Ctrl+C` 停止初始化容器，再改为后台运行。

## Docker 后台运行

设备绑定完成后使用：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  ghcr.io/ksamni/ctyun:latest
```

查看日志：

```bash
docker logs -f ctyun
```
## 推荐：使用 docker compose 部署

仓库根目录提供了 `docker-compose.yml`，适合长期在服务器上运行。首次绑定设备：

```bash
mkdir -p ctyun-data
# 把 accounts.json 放入 ./ctyun-data/accounts.json
docker compose run --rm ctyun    # 交互式输入短信验证码，看到"保活任务启动"后 Ctrl+C
```

确认绑定成功后改为后台常驻：

```bash
docker compose up -d
docker compose logs -f
```

升级镜像：

```bash
docker compose pull && docker compose up -d
```

compose 文件已包含的配置：

| 配置 | 作用 |
| --- | --- |
| `restart: unless-stopped` | 异常退出或宿主机重启后自动拉起；也覆盖了意外崩溃的场景 |
| `init: true` | 让 PID 1 正确转发 SIGTERM，`docker compose stop` 可优雅退出 |
| `stdin_open: true` | 供首次绑定交互输入短信验证码 |
| `TZ=Asia/Shanghai` | 容器内日志时间戳与本地一致（基础镜像已内置 tzdata） |
| `logging` | 日志滚动（10MB × 3），避免长期运行占满磁盘 |
| `security_opt` | `no-new-privileges`，禁止容器内进程提权 |
| `volumes` | 持久化 `/app/data`（含 `accounts.json` 与 `devices/` 设备码） |

> 注意：`docker compose run --rm ctyun` 会临时清空 `restart` 策略，因此首次绑定不会陷入重启循环。
> 未完成绑定直接 `docker compose up -d` 也是安全的：程序在非交互环境会跳过短信发送并低频重试，不会重复发短信。

## Docker 后台运行（不用 compose）

设备绑定完成后使用：

```bash
docker run -d \
  --name ctyun \
  --restart unless-stopped \
  -v "$(pwd)/ctyun-data:/app/data" \
  ghcr.io/ksamni/ctyun:latest
```

查看日志：

```bash
docker logs -f ctyun
```

## 兼容旧环境变量模式

单账号仍支持旧环境变量。首次绑定设备时同样使用 `-it` 输入短信验证码：

```bash
docker run -it --rm \
  --name ctyun-init \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  ghcr.io/ksamni/ctyun:latest
```

绑定完成后改为后台运行：

```bash
docker run -d \
  --name ctyun \
  -v "$(pwd)/ctyun-data:/app/data" \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD="你的密码" \
  -e DEVICECODE="web_你的设备码" \
  ghcr.io/ksamni/ctyun:latest
```

建议新部署优先使用 `accounts.json`，多账号管理更清晰，也更适合 Docker 持久化。

## 日志与保活

程序会为每个账号建立**长驻账号任务**，并周期性地重建"会话"：重新登录 → 重新获取云电脑列表 → 重新 connect 取连接票据 → 重建保活任务。日志格式会带上账号名和云电脑编号，便于区分：

```text
[account-a] 开始登录（第 2 段会话）。
[account-a][desktop-code] === 新周期开始，尝试连接 ===
[account-a][desktop-code] -> 收到保活校验
[account-a][desktop-code] -> 发送保活响应成功
[account-a] === 第 1 段会话运行 30.2 分钟，定时重建登录与保活会话 ===
```

### 为什么需要定时重建会话

云电脑的连接票据（`clinkLvsOutHost` 与配套证书、token）存在有效期。旧版本只在进程启动时获取一次票据，长时间运行后票据过期，服务端会在 WebSocket 刚刚就绪时立即断开，表现为每几秒重复出现：

```text
=== 新周期开始，尝试连接 ===
连接已就绪，保持 60 秒...
异常: The remote party closed the WebSocket connection without completing the close handshake.
```

由于纯 WebSocket 重连并不会刷新票据，这个循环无法自愈，必须重启进程（或容器）才能恢复。现在程序会按 `restartIntervalMinutes` 主动重建会话，在票据过期前换新票据；如果仍然检测到"就绪后立刻被断开"或"连续建连失败"，会立即重建整段会话，并按指数退避重试，不会高频空转。

建议把 `restartIntervalMinutes` 设为实测票据有效期的 1/2 ~ 1/3，且不小于两个 `keepAliveSeconds` 周期（程序会自动兜底该下限）。

## 说明

登录图形验证码识别接口方案来自 [sml2h3/ddddocr](https://github.com/sml2h3/ddddocr)。
