# 真三国无双：起源 资源工作台

面向 Steam PC 版《真三国无双：起源》的本地资源浏览、分类、预览和导出工具。项目参考了 `ReExtractor` 的三栏资源工作台思路，但解析对象是 Koei Tecmo 的 G1 系列格式，不使用 RE Engine 的 PAK/Mesh 假设。

仓库仅包含工具源码，不包含游戏本体、解包资源或导出资源。使用者需要自行合法拥有 Steam PC 版游戏。

## 快速开始

1. 启动工具，阅读首次启动时显示的使用说明。
2. 点击顶部高亮的“选择游戏目录”。
3. 选择同时包含 `DWORIGINS.exe` 和 `File` 文件夹的游戏根目录。
4. 扫描完成后，可从左侧目录树浏览资源，或在右侧按“角色 / 默认武器 / 动画”快捷预览。

如果游戏目录已有 `File/ASSET/FDataPackage/Dev/fdata_out`，工具会直接读取，无需再次解包。只有安装目录仅有 FData 包、没有展开资源时，模型和动画预览才需要先解包。

## 已实现

- 扫描游戏 `File` 目录，支持 20 万级资源库；分页、类型、资源域和文本筛选。
- 原生解析 `PDRK/IDRK0000` FData，支持单包和筛选结果批量解包；大资源的多段 Zlib 流会自动合并。
- 分类模型 (`G1M/G1MX`)、动画 (`G1A/G2A`)、骨架 (`G1S/G2S/RIGBIN`)、贴图 (`G1T/KTID`)、材质、碰撞、FData 包和视频。
- 通过文件头校验关键资源，不只依赖扩展名。
- 解析本游戏样本中的 `G1M_0039` / `G1MG_0045`（文件字节中显示为 `9300` / `5400`）：分块、包围盒、材质、顶点缓冲、索引缓冲和子网格。
- 模型线框预览：旋转、缩放、视角复位。
- 视口交互与 RE 解包工具一致：左键环绕、右键平移、中键平移、Alt+中键环绕、滚轮缩放；空白区域也可操作。
- 模型 OBJ 几何导出。
- 解析 `G1A_0042` 动画时长、骨骼轨道和动画类型，并提供轨道概览。
- 解析模型内部 G1MS 骨架；兼容曲线布局的 G1A 可播放、暂停和拖动真实骨骼动画，黄色显示已映射轨道。
- 解析 G1MG 骨骼调色板、顶点骨骼索引与权重；动画播放时通过 CPU 蒙皮驱动模型网格。
- 支持当前样本中的 G1A 曲线 opcode `2`、`6` 和《起源》新增的 `0x66` 九通道根运动布局（缩放、指数旋转、位移）。
- 通过 Steam 版 Blender 的官方 FBX 导出器生成标准二进制 FBX：骨骼模型（网格、蒙皮与 Bind Pose）、UE 动画文件、3ds Max 直接检查用的“模型＋动画”预览文件，以及独立动作镜头 FBX。
- 支持扩展名为 G1A、文件头为 `_A2G0400` 的 G2A 量化动画：读取 60 FPS、帧长、压缩骨骼表和 32 字节三次曲线块。
- 旧版/占位 G1A 仍会列出；无法验证的布局只显示元数据，不会伪造关键帧。
- 按 `资源类型/资源域` 分类复制当前筛选结果，同时生成 `manifest.json`。

## FData 说明

当前安装目录中的 `.fdata` 为 `PDRK/IDRK` 压缩容器。工具会读取 IDRK 资源 ID、记录边界、压缩尺寸与原始尺寸，解开连续的 16 KiB Zlib 数据块，并按文件头和已知哈希映射恢复扩展名。

每个包输出独立清单，记录资源 ID、源偏移、输出路径、大小和 SHA-256；不会修改游戏文件。

## 构建运行

需要 .NET 8 SDK 或更高版本：

```powershell
dotnet run --project .\DWOriginsAssetTool.csproj
```

发布单文件 Windows x64 版本：

```powershell
dotnet publish .\DWOriginsAssetTool.csproj -c Release -r win-x64 --self-contained true
```

## FBX 导入

- **Unreal Engine**：先导出并导入“当前模型 FBX”作为 Skeletal Mesh；再导出“角色动画 FBX”，导入时选择该 Skeletal Mesh 的 Skeleton、关闭 `Import Mesh`。这样动画不会重复创建网格。
- **3ds Max**：先导入模型 FBX，再将角色动画 FBX 加载到同一骨架；工具导出的两份文件保持 UE 标准分离流程。
- 工具会自动寻找 `Steam\steamapps\common\Blender\blender.exe`；FBX 由 Blender 写出，保留骨架层级、顶点权重、绑定姿势，并以 60 FPS 烘焙动画。
- 单位按游戏本体处理：G1M/G1A 的长度单位为厘米，进入 glTF/Blender 时自动换算为米，最终 FBX 导入 UE 后角色高度保持正确，不会出现 100 倍放大。

## 输出约定

工具默认以 RE 解包工具的方式，在 EXE 同级自动建立 `DWOrigins_Exports`，不把导出文件散落到游戏目录：

```text
DWOriginsAssetTool.exe
└─ DWOrigins_Exports/
   ├─ Model/       # 当前模型 FBX、OBJ
   ├─ Animation/   # UE 用纯角色动画 FBX
   ├─ Camera/      # 镜头 FBX 与 JSON/HTML 参数报告
   └─ Resources/   # 资源列表的分类导出与 manifest.json
```

分类导出不会修改游戏文件，结构如下：

```text
目标目录/
├─ Model/<资源域>/*.g1m
├─ Animation/<资源域>/*.g1a
├─ Skeleton/<资源域>/*
├─ Texture/<资源域>/*
└─ manifest.json
```

仅处理你合法拥有的游戏文件；导出的版权资源请勿传播。
