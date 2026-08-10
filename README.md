# 真三国无双：起源 资源工作台

面向 Windows 与 Steam PC 版《真三国无双：起源》的本地资源浏览、分类、预览和导出工具，解析对象是 Koei Tecmo 的 G1 系列格式。

项目定位是研究、预览和导出。它不包含游戏本体、解包资源或导出资源，也不提供游戏资源传播服务。

## [⬇ 下载最新版（Windows x64 单 EXE）](https://github.com/PieceMetal/DWOriginsAssetTool/releases/latest/download/DWOriginsAssetTool.exe)

## 安装

1. 下载 `DWOriginsAssetTool.exe`。
2. 将 EXE 放到可写目录运行。程序会在同级自动建立导出文件夹。
3. 首次启动时，在左侧选择同时包含 `DWORIGINS.exe` 和 `File` 文件夹的游戏根目录，然后点击“初始化游戏资源”。
4. 模型、动画和镜头 FBX 导出需要安装 [Blender](https://store.steampowered.com/app/365670/Blender/)；工具会自动检测 Steam 版，也可以手动选择 `blender.exe`。

当前版本：`v1.1.0`

## v1.0.3 更新

- 修正 FBX 厘米单位转换，UE 导入后角色与武器根骨保持 Rotation `0`、Scale `1`。
- 独立武器 FBX 已烘焙装备握持方向；附加到人物 `bone_0104` 后使用相对 Rotation `0`、Scale `1`。
- 初始化与 FBX 导出进度移动到中央时间轴上方，任务结束后自动隐藏且不改变视口布局。
- 更新内置使用说明与 UE 导入步骤；兼容 Blender 4.x 和 Blender 5.x 动画曲线接口。

## v1.0.2 测试版更新

- 增加从游戏 FData 包自动初始化完整基础资源树；多语言重复资源固定采用中文优先策略。
- 初始化支持按资源包断点续跑，Manifest 使用包相对路径生成唯一文件名，并校验已完成输出是否仍然存在。
- 只有解包零错误、正式名称/动作/镜头目录有效且 6,856 个基础动画全部解码通过后才写入完成标记。

## v1.0.1 更新

- 修复超过 250 帧的角色动画在 FBX 导出时被 Blender 默认场景范围截断的问题；现在按完整 Action 范围导出，保持游戏原始 60 FPS 与完整动画时长。

## v1.0.0 功能

- 直接扫描游戏目录，并以目录树浏览模型、动画、骨架、贴图、材质、碰撞、FData 包和视频资源。
- 原生解析 `PDRK/IDRK0000` FData，支持单包及筛选结果批量解包，并按资源类型、资源域和游戏本体动作表恢复目录。
- 从 KIDS 对象数据库恢复正式对象名称、角色动作归属和武器动作集，不依赖外部 FBX 文件夹分类。
- 解析 G1M/G1MX 模型、G1MS 骨架、G1A/G2A 动画、骨骼调色板、蒙皮索引与权重。
- 60 FPS 动画预览，支持播放、暂停、时间轴任意位置持续拖动、实时帧率、实体/线框/混合显示和骨骼树；镜头关键帧与配置事件集中在镜头曲线工具中查看。
- 可选镜头双视图：默认保持单视图，开启后左右视图同步同一帧，并可分别选择自由镜头、动画镜头、配置镜头模拟或最终合成；共享实时蒙皮网格，不重复进行 CPU 蒙皮。
- 支持 RE 工具与 UE 动画编辑器两套视口操作；镜头 HUD 显示按累计路径长度计算的摄像机位移进度。
- 镜头轨迹按播放进度逐段显示，重新播放时清空并重新生成；拖动时间轴会重建到当前帧。
- 效果预览器可自由选择角色、武器和动画；武器保持为独立模型并自动挂到人物装备骨骼。
- 独立镜头预览器按游戏本体固定角色模型、角色动画和镜头；已确认 127 组动作镜头关联（含 3 组本体引用明确、时长短于人物动作的局部镜头段）。
- 支持自由镜头、动画镜头、摄像机位置示意、镜头轨迹与实时 FOV/位置/速度数据。
- 配置镜头分析会读取本体 KIDS DefaultPlayer 的正式字段，包括 `UC_AM_FocalLength`、`UC_AM_HeightOffset`、`UC_AM_FollowCameraNear/Far`、`UC_AM_TargetFov` 等。零售 `FollowCamera2` 的部分空间关系已经恢复，但脚本运行时实参来源、目标胶囊、战斗目标与地形碰撞尚未完整还原，因此界面明确标为“配置镜头（近似）”，不把静态字段直接冒充最终实机镜头。动作全局骨骼 1 目前只提取相对首帧的前后位移分量，用于分析前冲、后撤等控制器位移；绝对骨骼偏移、横向技能摆动、高度和 Root 旋转不会传给镜头。
- 镜头曲线工具用菱形表示动画镜头原始 K 帧、方块表示配置事件、色条表示配置持续区间；插值帧不会显示成原始 K 帧。
- 通过用户安装的 Blender 导出 UE 与 3ds Max 可用的二进制 FBX。
- UE 镜头导出不是只烘焙配置层：独立 G1A 动画镜头、配置镜头和最终合成都以工具视口求值结果按 60 FPS 烘焙；最终合成包含动画镜头与配置层共同作用后的机位、旋转、Roll 和动态 FOV。
- 发布包为单个自包含 EXE，Blender 转换脚本已内嵌，不需要单独安装组件。

## 使用方法

1. 启动工具并阅读首次显示的使用说明。
2. 在左侧选择游戏目录并初始化游戏资源，等待校验和扫描完成。
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
2. 单独导入武器模型 FBX，将武器 Skeletal Mesh Component 附加到人物 `bone_0104`；新版已烘焙握持朝向，相对 Rotation 使用 `0`、Scale 使用 `1`。
3. 导入角色动画 FBX 时关闭 `Import Mesh`，并选择角色模型已经创建的 Skeleton。
4. 在 Sequencer 中选择 Cine Camera Actor，再导入镜头 FBX。

测试新版导出时应删除 UE 中旧模型与旧 Skeleton 后重新导入，避免复用旧的单位或骨骼轴向数据。初始化资源时任务进度显示在中央视口下方；加载时间轴后，导出进度显示在时间轴上方，完成后自动隐藏。

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
