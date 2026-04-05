### windows用户直接下载Releases执行即可。

### 编译 Linux / Windows 二进制

项目已在 [`CtYun/CtYun.csproj`](CtYun/CtYun.csproj) 中声明常用运行时标识，现可直接编译以下平台：

- `linux-x64`
- `linux-arm`
- `linux-arm64`
- `win-x64`
- `win-arm64`

在项目根目录执行：

#### Linux x64

```
dotnet publish CtYun/CtYun.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -o publish/linux-x64
```

输出文件为 `publish/linux-x64/CtYun`。

#### Linux ARM32

```
dotnet publish CtYun/CtYun.csproj -c Release -r linux-arm --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -o publish/linux-arm
```

输出文件为 `publish/linux-arm/CtYun`。

#### Linux ARM64

```
dotnet publish CtYun/CtYun.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -o publish/linux-arm64
```

输出文件为 `publish/linux-arm64/CtYun`。

#### Windows ARM64

```
dotnet publish CtYun/CtYun.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -o publish/win-arm64
```

输出文件为 `publish/win-arm64/CtYun.exe`。

> 说明：当前示例统一使用 `-p:PublishAot=false` 进行跨平台发布，若需要在目标平台启用 Native AOT，需要对应平台工具链支持。

### GitHub Actions 自动编译与 Release 发布

项目已包含自动工作流 [`.github/workflows/dotnet-desktop.yml`](.github/workflows/dotnet-desktop.yml)，支持以下触发方式：

- push 到 `master`
- 提交 pull request 到 `master`
- push `v*` 标签
- 手动触发 `workflow_dispatch`

Actions 会自动构建以下平台产物：

- `linux-x64`
- `linux-arm`
- `linux-arm64`
- `win-x64`
- `win-arm64`

每个平台会单独执行 [`dotnet publish`](.github/workflows/dotnet-desktop.yml:50)，并上传对应的压缩产物 artifact。

当推送 `v*` 标签时，还会自动执行 GitHub Release 发布流程，将构建产物作为附件上传到 Release。

如果仓库已配置 `DOCKER_USERNAME` 与 `DOCKER_PASSWORD` secrets，还会继续执行 [Docker 多架构镜像构建](.github/workflows/dotnet-desktop.yml:112)，推送：

- `ctyun:版本号`
- `ctyun:latest`

### 多账号使用说明

程序现在同时支持“环境变量多账号”与“配置文件多账号”两种方式，账号解析入口位于 [`ResolveAccounts()`](CtYun/Program.cs:198)。

#### 方式一：环境变量多账号

可通过以下环境变量批量传入多个账号，使用分号或换行分隔：

- `APP_USERS`
- `APP_PASSWORDS`
- `DEVICECODES`
- `APP_NAMES`

示例：

```
export APP_USERS='13800000001;13800000002'
export APP_PASSWORDS='密码1;密码2'
export DEVICECODES='web_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx;web_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy'
export APP_NAMES='账号A;账号B'
./CtYun
```

如果只传入单账号，也仍然兼容以下旧环境变量：

- `APP_USER`
- `APP_PASSWORD`
- `DEVICECODE`
- `APP_NAME`

#### 方式二：配置文件多账号

在程序运行目录创建 [`accounts.json`](accounts.json)，内容示例：

```json
{
  "accounts": [
    {
      "name": "账号A",
      "user": "13800000001",
      "password": "密码1",
      "deviceCode": "web_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
    },
    {
      "name": "账号B",
      "user": "13800000002",
      "password": "密码2",
      "deviceCode": "web_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy"
    }
  ]
}
```

Android/Termux 下可将 [`accounts.json`](accounts.json) 放在用户主目录；桌面系统下默认放在程序当前目录。

#### 优先级

程序按以下顺序解析账号：

1. 多账号环境变量 `APP_USERS` / `APP_PASSWORDS`
2. 单账号环境变量 `APP_USER` / `APP_PASSWORD`
3. [`accounts.json`](accounts.json)
4. 交互输入

多个账号会并发登录并分别启动各自的保活任务。

首次登录需要绑定新设备接收验证码,windows生成的设备信息在DeviceCode.txt文件中

### docker使用指南
> :warning: **提示：** docker第一次运行不要后台执行，不要加-d运行，要添加-it， 第一次软件会生成一个新的设备信息，需要接收短信来进行风控校验，需手动输入，提示设备 **保活任务启动** 即可后台运行。

设备号DEVICECODE是web_加上随机32大小写字母数字_
例如 **web_L53itptDslz6manpE8Uq2Op1OEoKi85t** 
不要填写案例，请自己生成或更改上方案例

Linux 可使用以下代码生成
```
echo "web_$(cat /dev/urandom | tr -dc 'a-zA-Z0-9' | fold -w 32 | head -n 1)"
```
```
//第一次初始化运行这个。
docker run -it \
  --name ctyun \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD='你的密码' \
  -e DEVICECODE='设备Id' \
  su3817807/ctyun:latest

```

```
//第一次运行不要加-d
docker run -d \
  --name ctyun \
  -e APP_USER="你的账号" \
  -e APP_PASSWORD='你的密码' \
  -e DEVICECODE='设备Id' \
  su3817807/ctyun:latest

```
### 查看日志检查是否登录并连接成功。

```
docker logs -f ctyun

```


验证码识别api方案来自 https://github.com/sml2h3/ddddocr
