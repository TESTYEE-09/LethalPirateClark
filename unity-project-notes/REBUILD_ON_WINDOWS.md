# Rebuild the `stilllife` asset bundle on Windows (Unity 2022.3.62f1)

**Why:** Lethal Company now runs on **Unity 2022.3.62f1**. The bundle currently
shipped in v2.0.0 was baked by an older editor (2022.3.9), so the game refuses to
load it (`AssetBundle ... not compatible with this newer version of the Unity
runtime` → `LoadFromFile returned NULL` → enemy never registers → never appears in
Imperium). Everything else in v2.0.0 is correct (the DLL, the netcode RPC weave).
The **only** remaining step is to re-bake the bundle with a real 2022.3.62f1 editor.

This is a one-time, ~20-minute job on your Windows PC.

---

## 0. What you need
- Windows PC with **Lethal Company installed via Steam**.
- **Unity Hub** + **Unity 2022.3.62f1** (the exact version the game uses).
- **git** (to clone this repo) — or just download the repo ZIP from GitHub.

> On Windows the editor builds Windows asset bundles natively — you do **not** need
> any extra "build support" module. That whole headache was Mac-only.

---

## 1. Install Unity 2022.3.62f1
1. Open **Unity Hub** → **Installs** → **Install Editor** → **Archive** →
   "download archive" → find **2022.3.62f1** → Install.
   (Direct: `unityhub://2022.3.62f1/4af31df58517`)
2. No extra modules are required for this (Windows target is built in). Installing
   **Windows Build Support (IL2CPP)** is fine but unnecessary.

## 2. Get the project
```
git clone https://github.com/TESTYEE-09/LethalPirateClark.git
```
The Unity project is the folder **`LethalPirateClark/unity/StillLifeUnity`**.
(Or download the repo ZIP from GitHub and unzip it.)

## 3. Drop in the game's DLLs (the only thing not in the repo — copyrighted)
The build needs Lethal Company's own types (`EnemyType`, `EnemyAI`, `TerminalNode`…).
Copy them from your game install into the project's `Assets/Plugins` folder.

- Game files: Steam → **Lethal Company** → right-click → **Manage ▸ Browse local
  files** → `Lethal Company\Lethal Company_Data\Managed\`
- Copy these into `unity\StillLifeUnity\Assets\Plugins\`:
  - `Assembly-CSharp.dll`  ← **required** (publicized version preferred, see note)
  - `Assembly-CSharp-firstpass.dll`
  - `ClientNetworkTransform.dll`  (if present)
  - `DunGen.dll`
  - `Facepunch.Steamworks.Win64.dll`
- **Do NOT copy** `UnityEngine*.dll`, `System*.dll`, `mscorlib.dll`, or
  `Unity.Netcode.*` — the editor and the project's packages already provide those,
  and duplicates cause errors.

> The `Assets/Plugins/*.dll.meta` files are already in the repo, so just drop the
> `.dll` files in with their exact names — the GUIDs will line up and the prefab's
> game-type references resolve automatically.
>
> **Publicized Assembly-CSharp (recommended):** so the builder can set every
> `EnemyType` field. Easiest source = the NuGet package `LethalCompany.GameLibs.Steam`
> (already publicized), or run BepInEx AssemblyPublicizer on your game's
> `Assembly-CSharp.dll`. A non-publicized DLL still builds; some fields just fall
> back to defaults.

## 4. Open the project and let it compile
1. Unity Hub → **Open** → select `unity/StillLifeUnity`. Open it with **2022.3.62f1**.
   (If Hub warns the project was made with a different version, that's expected —
   open with 2022.3.62f1 anyway; that's the whole point.)
2. Wait for the import + script compile to finish. The **Console** should have **no
   red errors**. (A VFX-graph / HDRP warning is harmless.)
   - If you see `EnemyType not found` or similar, a game DLL is missing — recheck step 3.

## 5. Bake the bundle
Menu bar → **StillLife ▸ Build Everything**.
- It rebuilds the prefab + EnemyType + terminal scan entry and bakes the bundle for
  **StandaloneWindows64**.
- A popup says: `Done! Bundle written to: ...\AssetBundles\stilllife`.

Output file: **`unity\StillLifeUnity\AssetBundles\stilllife`** (~1 MB).

> Quick sanity check: open `stilllife` in a text editor; the first line should read
> `UnityFS` and you should see `2022.3.62f1` near the top. That confirms it was baked
> by the right editor.

## 6a. Test it immediately (no waiting on a new release)
Drop the freshly-baked `stilllife` straight into your r2modman profile, next to the
v2.0.0 DLL — **overwrite** the old one:
```
...\r2modmanPlus-local\LethalCompany\profiles\Clark\BepInEx\plugins\Unknown-LethalPirateClark\StillLife\stilllife
```
Launch the game. In `BepInEx\LogOutput.log` you should now see:
```
[StillLife] Bundle loaded. Assets: ...
[StillLife] Registered 'Pirate Clark' from bundle — rarity 40, max alive 1, bestiary=yes.
```
Then he'll be in the **Imperium** spawn list (search "Pirate" or "Clark").

## 6b. Send it back so it ships in the release
Upload the rebuilt `stilllife` file (e.g. drag it into the chat, or commit it). I'll
drop it into `dist/`, cut **v2.0.1**, and update the GitHub release + Thunderstore so
nobody has to rebuild again.

---

## If you'd rather hand this to an AI assistant on the Windows PC
Paste this prompt:

> I have the repo `https://github.com/TESTYEE-09/LethalPirateClark` cloned locally and
> Unity **2022.3.62f1** installed. I need to re-bake the Lethal Company asset bundle
> because the committed one was built with the wrong Unity version and the game won't
> load it. Open `unity/StillLifeUnity`, make sure the game's managed DLLs
> (`Assembly-CSharp.dll` + the deps listed in `Assets/Plugins/PUT_GAME_DLLS_HERE.txt`)
> are copied from my Lethal Company install into `Assets/Plugins/`, confirm the project
> compiles with no errors, then run the editor menu **StillLife ▸ Build Everything**
> (or batch: `Unity.exe -batchmode -quit -projectPath <path>\unity\StillLifeUnity
> -executeMethod StillLifeBuilder.BuildFromCommandLine -logFile -`). The output bundle
> is `AssetBundles/stilllife`; verify its header says `2022.3.62f1`, then copy it into
> my r2modman profile at
> `...\profiles\Clark\BepInEx\plugins\Unknown-LethalPirateClark\StillLife\stilllife`,
> overwriting the old one.
