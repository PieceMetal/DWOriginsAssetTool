# 真三国无双：起源 资源工作台

面向 Windows 与 Steam PC 版《真三国无双：起源》的本地资源浏览、分类、预览和导出工具，解析对象是 Koei Tecmo 的 G1 系列格式。

项目定位是研究、预览和导出。它不包含游戏本体、解包资源或导出资源，也不提供游戏资源传播服务。

## [⬇ 下载 v1.0.0（Windows x64）](https://github.com/PieceMetal/DWOriginsAssetTool/releases/latest/download/DWOriginsAssetTool-v1.0.0-win-x64.zip)

## 安装

1. 下载并解压 ZIP；压缩包内只有 `DWOriginsAssetTool.exe`。
2. 将 EXE 放到可写目录运行。程序会在同级自动建立导出文件夹。
3. 首次启动后点击高亮的“选择游戏目录”，选择同时包含 `DWORIGINS.exe` 和 `File` 文件夹的游戏根目录。
4. 模型、动画和镜头 FBX 导出需要安装 [Blender](https://store.steampowered.com/app/365670/Blender/)；工具会自动检测 Steam 版，也可以手动选择 `blender.exe`。

当前版本：`v1.0.0`

## v1.0.0 功能

- 直接扫描游戏目录，并以目录树浏览模型、动画、骨架、贴图、材质、碰撞、FData 包和视频资源。
- 原生解析 `PDRK/IDRK0000` FData，支持单包及筛选结果批量解包，并按资源类型、资源域和游戏本体动作表恢复目录。
- 从 KIDS 对象数据库恢复正式对象名称、角色动作归属和武器动作集，不依赖外部 FBX 文件夹分类。
- 解析 G1M/G1MX 模型、G1MS 骨架、G1A/G2A 动画、骨骼调色板、蒙皮索引与权重。
- 60 FPS 动画预览，支持播放、暂停、时间轴拖动、实时帧率、实体/线框/混合显示和骨骼树。
- 效果预览器可自由选择角色、武器和动画；武器保持为独立模型并自动挂到人物装备骨骼。
- 独立镜头预览器按游戏本体固定角色模型、角色动画和镜头；已确认 124 组动作镜头关联。
- 支持自由镜头、动画镜头、摄像机位置示意、镜头轨迹与实时 FOV/位置/速度数据。
- 通过用户安装的 Blender 导出 UE 与 3ds Max 可用的二进制 FBX。
- 发布包为单个自包含 EXE，Blender 转换脚本已内嵌，不需要单独安装组件。

## 使用方法

1. 启动工具并阅读首次显示的使用说明。
2. 选择游戏目录并等待资源扫描完成。
3. 从左侧目录树选择资源，或在右侧“效果预览器”自由组合角色、武器和动画。
4. 在“镜头预览”中选择固定镜头动作；工具会自动加载对应角色、默认武器、动画和镜头并立即播放。
5. 使用右侧导出区域分别导出角色模型、武器模型、角色动画或镜头 FBX。

如果游戏目录已有 `File/ASSET/FDataPackage/Dev/fdata_out`，工具会直接读取，无需再次解包。只有安装目录仅有 FData 包、没有展开资源时，模型和动画预览才需要先解包。

## FBX 导出说明

- “导出模型 FBX”输出角色模型；当前预览已加载武器时，会在同目录额外输出一个独立武器模型 FBX，不会合并两者。
- “导出角色动画 FBX”只包含骨骼层级与动画曲线，不包含人物网格。
- “导出镜头动画 FBX”只输出摄像机动画，并生成 JSON/HTML 镜头参数报告，不重复导出角色或武器。
- 模型与动画使用相同骨架层级；Blender 的转换容器会命名为 `Armature`，避免 UE 将模型名识别成额外根骨骼。
- 动画按 60 FPS 烘焙；单位在 G1、glTF、Blender 和 UE 之间统一换算。

### Unreal Engine

1. 导入角色模型 FBX，创建 Skeletal Mesh 与 Skeleton。
2. 单独导入武器模型 FBX，并按需要附加到人物装备插槽。
3. 导入角色动画 FBX 时关闭 `Import Mesh`，并选择角色模型已经创建的 Skeleton。
4. 在 Sequencer 中选择 Cine Camera Actor，再导入镜头 FBX。

### 3ds Max

先导入角色模型 FBX，再把角色动画 FBX 加载到同一骨架。武器模型是独立 FBX，可单独导入和约束。

## 输出目录

程序会在 EXE 同级建立 `DWOrigins_Exports`：

```text
DWOriginsAssetTool.exe
└─ DWOrigins_Exports/
   ├─ Model/       # 角色模型、独立武器模型和 OBJ
   ├─ Animation/   # 无人物网格的角色动画 FBX
   ├─ Camera/      # 镜头 FBX 与 JSON/HTML 参数报告
   └─ Resources/   # 分类资源与 manifest.json
```

解包和分类导出不会修改游戏文件。

## 源码构建

环境要求：Windows 10/11、.NET 8 SDK。只有导出 FBX 时需要 Blender。

```powershell
git clone https://github.com/PieceMetal/DWOriginsAssetTool.git
cd DWOriginsAssetTool
dotnet build -c Release
dotnet run
```

发布 Windows x64 单文件版本：

```powershell
dotnet publish -c Release
```

## 第三方组件

- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET)：MIT License
- [Blender](https://www.blender.org/)：由用户独立安装并遵守 Blender 自身许可证

## 免责声明

本项目与 Koei Tecmo Games 及其关联公司无关。游戏名称、商标和资源归各自权利人所有。请仅处理你合法拥有的游戏文件，并遵守所在地法律与游戏服务条款；请勿传播导出的版权资源。
