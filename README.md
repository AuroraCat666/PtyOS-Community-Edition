# PtyOS Community Edition

基于 [RE-Phigros](https://github.com/REPhigrOS-DevTeam/re-phigros) 的社区修改版本。

## 项目说明

本项目是 RE-Phigros 的社区分支，添加了以下功能：

- 多人联机对战模式
- 社区账号系统（网页端注册/登录）
- 公告系统（支持邮件通知）
- 排行榜功能
- 自定义服务器支持

## 仓库结构

本仓库包含**两个独立的部分**：

| 目录 | 内容 |
|------|------|
| 根目录 | **客户端**（Unity 项目，re-phigros 客户端） |
| [`Server/`](./Server/) | **服务端**（.NET 8 社区服务器，独立部署，与客户端分开） |

> `Server/` 目录是独立的服务端程序（HTTP 社区服务 + TCP 联机服务），不依赖客户端；客户端通过服务器地址连接它。部署方式见 [`Server/说明文档.md`](./Server/说明文档.md)。

## 技术栈

### 客户端
- Unity 6000.5.8f1
- C#

### 服务端
- .NET 8.0
- C#
- MailKit（邮件服务）

## 编译说明

### 客户端（Unity）

1. 使用 Unity Hub 安装 Unity 6000.5.8f1
2. 安装 Android Build Support 模块（如需打包 APK）
3. 打开项目文件夹 `re-phigros-main`
4. 等待 Unity 导入资源完成
5. 打开 `Assets/Scenes/EntryScene.unity` 即可运行

### 服务端

```bash
cd RPGR-Server
dotnet build -c Release
```

## 部署说明

### 服务端部署

服务端使用 Docker 部署：

```bash
cd RPGR-Server
docker-compose up -d
```

配置参数（通过环境变量或命令行参数）：
- `--smtp-host`: SMTP 服务器地址
- `--smtp-port`: SMTP 端口
- `--smtp-user`: SMTP 用户名
- `--smtp-pass`: SMTP 密码
- `--smtp-from`: 发件人邮箱

### 客户端配置

修改 `Assets/Scripts/Network/RepAPI.cs` 中的服务器地址：

```csharp
public static readonly string BaseUrl = "https://your-server.com";
```

## 许可证

本项目遵循原项目 **GPL-3.0** 许可证。

原项目：[REPhigrOS-DevTeam/re-phigros](https://github.com/REPhigrOS-DevTeam/re-phigros)

## 免责声明

本项目仅供学习交流使用，请勿用于商业用途。

游戏资源版权归原权利人所有。
