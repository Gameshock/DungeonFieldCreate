using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class CaveGeneratorTilemapV2 : MonoBehaviour
{
    public static CaveGeneratorTilemapV2 I { get; private set; }

    public Field_SO Field;

    [Header("サイズ設定")]
    public int width = 50;
    public int height = 50;

    [Header("Tilemap & Tiles")]
    public Tilemap tilemap;
    public TileBase floorTile;   // 0
    public TileBase floorTile2;  // 0
    public TileBase floorTile3;   // 0
    public TileBase wallTile;    // 1
    public TileBase bedrockTile; // 2（壊れない壁）
    public TileBase startTile;
    public TileBase goalTile;
    public GameObject playerPrefab;

    [Header("床生成ノイズ")]
    public int seed = 1234;
    [Range(0.02f, 0.3f)] public float noiseScale = 0.1f;
    [Range(-1f, 1f)] public float threshold = 0f;

    [Header("床の塊 最小サイズ")]
    public int minRegionSize = 20;

    [Header("内部壁(1) 生成パラメータ")]
    [Range(0.02f, 0.4f)] public float wallNoiseScale = 0.18f;
    [Range(0f, 1f)] public float wallNoiseThreshold = 0.62f;
    [Range(0f, 0.5f)] public float wallSeedJitter = 0.12f; // 0±揺らし
    [Range(0, 3)] public int interiorWallKeepAwayFromOuter = 1; // -1/2から離す距離
    [Range(0, 6)] public int wallSmoothIterations = 2;
    [Range(0, 8)] public int caBirth = 5; // 近傍壁数>=で誕生
    [Range(0, 8)] public int caDeath = 2; // 近傍壁数<で消滅

    [Header("宝箱")]
    public TreasureBoxList_SO treasureBoxList;
    [SerializeField] private Transform treasureParent;
    [SerializeField] private GameObject treasurePrefab;
    [SerializeField] private int treasureCount = 10;
    private string itemResources = "03_ScriptableObject/Items";
    private CreateItem_SO[] Items;

    [Header("敵情報")]
    public EnemyGroup_SO enemyGroup;
    private Enemy_SO[] enemyDB;
    [SerializeField] private Transform EnemiesParent;
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] int enemyCount = 5;
    [HideInInspector] public List<GameObject> spawnedEnemies = new();

    // 内部データ
    public int[,] map;
    [HideInInspector] public Vector2Int startPos;
    [HideInInspector] public Vector2Int goalPos;
    public enum TileType { Void = -1, Wall = 1, Bedrock = 2, Floor = 0, Start = 3, Goal = 4 }
    [HideInInspector] public GameObject player;

    private InventoryManager inventoryManager;
    private Dictionary<Vector3Int, int> wallHits = new Dictionary<Vector3Int, int>();
    [Header("壁破壊閾値")]
    public int wallBreakThreshold = 3;


    private void Awake()
    {
        if (I == null) I = this;
        else Destroy(gameObject);

        Items = Resources.LoadAll<CreateItem_SO>(itemResources);

        inventoryManager = FindAnyObjectByType<InventoryManager>();
    }

    private void Start()
    {
        Init();
    }

    private void SetFieldAssets()
    {
        Field = FieldManager.I.SetFieldGroup();

        if (Field == null)
        {
            Debug.LogError("フィールドが設定されていません");
            return;
        }

        width = Field.width;
        height = Field.height;

        noiseScale = Field.noiseScale;
        threshold = Field.threshold;
        minRegionSize = Field.minRegionSize;

        wallNoiseScale = Field.wallNoiseScale;
        wallNoiseThreshold = Field.wallNoiseThreshold;
        wallSeedJitter = Field.wallSeedJitter;
        interiorWallKeepAwayFromOuter = Field.interiorWallKeepAwayFromOuter;
        wallSmoothIterations = Field.wallSmoothIterations;
        caBirth = Field.caBirth;
        caDeath = Field.caDeath;

        treasureBoxList = Field.treasureBoxList;
        treasureCount = Field.TreasureCount;

        enemyGroup = Field.enemyGroup;
        enemyCount = Field.enemyCount;
    }

    /// <summary>
    /// 初期化
    /// </summary>
    public void Init()
    {
        SetFieldAssets();
        ClearEnemies();
        ClearTreasures();
        GenerateMap();
        BuildTileMap();

        // TODO: 後の改修でプレイヤーの種類を増やした時選んだプレイヤープレハブを入れる
        // プレイヤー生成
        if (playerPrefab != null && player == null)
        {
            player = Instantiate(playerPrefab, (Vector3Int)startPos, Quaternion.identity);
            FieldUI.I.InitUI();
        }
        else
        {
            player.transform.position = (Vector3Int)startPos;
        }
        // FocusPlayerCamera.I.transform.position = (Vector3Int)startPos + new Vector3(0, 0, -10);

#if UNITY_EDITOR
        DebugUI.I.DebugUI_Start();
#endif
    }

    /// <summary>
    /// シード値を生成
    /// </summary>
    /// <returns></returns>
    private int RandomSeedValue()
    {
        string seedStr = "";
        for (int i = 0; i < 7; i++)
        {
            seedStr += Random.Range(0, 10);
        }

        return int.Parse(seedStr);
    }

    // ================== 生成全体 ==================
    private void GenerateMap()
    {
        seed = RandomSeedValue();
        map = new int[width, height];

        // 1) 全面 -1
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                map[x, y] = -1;

        // 2) 外周より1マス小さくアメーバ状に床0
        GenerateAmebaFloor();

        // 3) 小さな床島を削除（塊で配置）
        RemoveSmallFloorRegions(minRegionSize);

        // 4) 床0と-1が隣接する-1を壊れない壁2へ
        ApplyOuterBedrock();

        // 5) 床内部に自然な壁1（障害物）を生成
        GenerateInteriorWalls();

        // 6) さらに微調整（任意）
        CellularSmoothFloorToWalls();

        // 7) スタート・ゴール地点を決定
        PlaceStartAndGoal();

        // 8) 宝箱を生成
        PlaceTreasures();

        // 9) 敵を生成
        PlaceEnemies();
    }

    // =============== 2) アメーバ床 ===============
    private void GenerateAmebaFloor()
    {
        float cx = width / 2f, cy = height / 2f;
        float maxR = Mathf.Min(width, height) / 2f;

        for (int x = 1; x < width - 1; x++)
            for (int y = 1; y < height - 1; y++)
            {
                // 外周1マスは床を作らない（必ず-1を残す）
                if (x < 1 || y < 1 ||
                    x > width - 1 - 1 || y > height - 1 - 1)
                    continue;

                float n = Mathf.PerlinNoise((x + seed) * noiseScale, (y + seed) * noiseScale);
                float dx = (x - cx) / maxR, dy = (y - cy) / maxR;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (n - dist * 0.5f > threshold) map[x, y] = 0; // 床
            }
    }

    // =============== 3) 小さな床島の削除 ===============
    private void RemoveSmallFloorRegions(int minSize)
    {
        int[,] vis = new int[width, height];
        int id = 0;
        for (int x = 1; x < width - 1; x++)
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] == 0 && vis[x, y] == 0)
                {
                    id++;
                    var region = FloodFillFloor(x, y, id, vis);
                    if (region.Count < minSize)
                        foreach (var p in region) map[p.x, p.y] = -1;
                }
            }
    }
    private List<Vector2Int> FloodFillFloor(int sx, int sy, int id, int[,] vis)
    {
        var q = new Queue<Vector2Int>();
        var reg = new List<Vector2Int>();
        vis[sx, sy] = id; q.Enqueue(new Vector2Int(sx, sy));
        while (q.Count > 0)
        {
            var p = q.Dequeue(); reg.Add(p);
            for (int d = 0; d < 4; d++)
            {
                int nx = p.x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = p.y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx <= 0 || ny <= 0 || nx >= width - 1 || ny >= height - 1) continue;
                if (map[nx, ny] == 0 && vis[nx, ny] == 0) { vis[nx, ny] = id; q.Enqueue(new Vector2Int(nx, ny)); }
            }
        }
        return reg;
    }

    // =============== 4) 外周 壊れない壁2 ===============
    private void ApplyOuterBedrock()
    {
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                if (map[x, y] == -1 && HasNeighborOf(x, y, 0)) // -1 が床0に隣接
                    map[x, y] = 2;                            // → 2 に変換
            }
    }

    // =============== 5) 内部壁1（床の上に自然配置） ===============
    private void GenerateInteriorWalls()
    {
        // 壁のシード（床0→1 へ一部変換）
        int[,] tmp = (int[,])map.Clone();

        for (int x = 1; x < width - 1; x++)
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] != 0) continue; // 床のみ対象

                // 外縁（-1/2）から一定距離は壁を生やさない
                if (IsNearType(x, y, -1, interiorWallKeepAwayFromOuter)) continue;
                if (IsNearType(x, y, 2, interiorWallKeepAwayFromOuter)) continue;

                float n = Mathf.PerlinNoise((x + seed) * wallNoiseScale, (y - seed) * wallNoiseScale);
                n += (Random.value - 0.5f) * wallSeedJitter;

                if (n > wallNoiseThreshold)
                    tmp[x, y] = 1; // 壁シード
            }

        // セルオートマトンで塊化
        for (int it = 0; it < wallSmoothIterations; it++)
        {
            int[,] next = (int[,])tmp.Clone();
            for (int x = 1; x < width - 1; x++)
                for (int y = 1; y < height - 1; y++)
                {
                    if (tmp[x, y] == 2) continue; // bedrockは触らない

                    int nb = CountNeighborsOf(tmp, x, y, 1); // 壁近傍

                    if (tmp[x, y] == 1) // 生存判定
                    {
                        if (nb < caDeath) next[x, y] = 0; // 消滅→床に戻す
                    }
                    else if (tmp[x, y] == 0) // 誕生判定（床→壁）
                    {
                        if (nb >= caBirth) next[x, y] = 1;
                    }
                }
            tmp = next;
        }

        // 再度、外縁からの距離制約（縁ギリの壁を撤去）
        for (int x = 1; x < width - 1; x++)
            for (int y = 1; y < height - 1; y++)
            {
                if (tmp[x, y] == 1 && (IsNearType(x, y, -1, interiorWallKeepAwayFromOuter) || IsNearType(x, y, 2, interiorWallKeepAwayFromOuter)))
                    tmp[x, y] = 0;
            }

        map = tmp;
    }

    // =============== 6) 仕上げの平滑化（任意） ===============
    private void CellularSmoothFloorToWalls()
    {
        int[,] nm = (int[,])map.Clone();
        for (int x = 1; x < width - 1; x++)
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] == 0) // 床の凹みを少し埋める
                {
                    int wallsAround = GetNeighborCount(x, y, 1) + GetNeighborCount(x, y, 2);
                    if (wallsAround > 5) nm[x, y] = 1;
                }
            }
        map = nm;
    }

    // ================== ヘルパー ==================
    private bool HasNeighborOf(int cx, int cy, int target)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (map[nx, ny] == target) return true;
            }
        return false;
    }
    private bool IsNearType(int cx, int cy, int target, int radius)
    {
        if (radius <= 0) return false;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (map[nx, ny] == target) return true;
            }
        return false;
    }
    private int GetNeighborCount(int cx, int cy, int target)
    {
        int c = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (map[nx, ny] == target) c++;
            }
        return c;
    }
    private int CountNeighborsOf(int[,] src, int cx, int cy, int target)
    {
        int c = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (src[nx, ny] == target) c++;
            }
        return c;
    }

    // ================== スタート・ゴール地点 ==================
    /// <summary>
    /// スタート・ゴール地点を決定
    /// </summary>
    private void PlaceStartAndGoal()
    {
        startPos = new Vector2Int(width / 2 + 1, height / 2 + 1);
        map[startPos.x, startPos.y] = (int)TileType.Start;
        ClearAround(startPos);
        goto GoalSearch;

    GoalSearch:
        // 四隅のどれかからスタート 下記番号振り分け
        // 0 1
        // 2 3
        int randomGoalPos = Random.Range(0, 4);
        List<Vector2Int> candidates = new();
        bool found = false;

        while (!found)
        {
            switch (randomGoalPos)
            {
                case 0:
                    // ゴール：左上から探すしランダム
                    for (int x = 1; x < width / 3; x++)
                        for (int y = height - 2; y > height * 2 / 3; y--)
                        {
                            if (map[x, y] == (int)TileType.Floor) candidates.Add(new Vector2Int(x, y));
                        }
                    break;
                case 1:
                    // ゴール：右上から探すしランダム
                    for (int x = width - 2; x > width * 2 / 3; x--)
                        for (int y = height - 2; y > height * 2 / 3; y--)
                        {
                            if (map[x, y] == (int)TileType.Floor) candidates.Add(new Vector2Int(x, y));
                        }
                    break;
                case 2:
                    // ゴール：左下から探すしランダム
                    for (int x = 1; x < width / 3; x++)
                        for (int y = 1; y < height / 3; y++)
                        {
                            if (map[x, y] == (int)TileType.Floor) candidates.Add(new Vector2Int(x, y));
                        }
                    break;
                case 3:
                    // ゴール：右下から探すしランダム
                    for (int x = width - 2; x > width * 2 / 3; x--)
                        for (int y = 1; y < height / 3; y++)
                        {
                            if (map[x, y] == (int)TileType.Floor) candidates.Add(new Vector2Int(x, y));
                        }
                    break;
            }

            if (candidates.Count > 0)
            {
                int index = Random.Range(0, candidates.Count);
                goalPos = candidates[index];
                found = true;
            }
            else
            {
                found = false;
            }
        }


        map[goalPos.x, goalPos.y] = (int)TileType.Goal;
    }

    /// <summary>
    /// 周囲のセルをクリア
    /// </summary>
    /// <param name="pos"></param>
    private void ClearAround(Vector2Int pos)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int nx = pos.x + dx;
                int ny = pos.y + dy;

                if (nx <= 0 || nx >= width - 1 || ny <= 0 || ny >= height - 1) continue;

                if (map[nx, ny] == (int)TileType.Wall) map[nx, ny] = (int)TileType.Floor;
            }
    }

    // ================== アイテムグリッド登録 ==================
    /// <summary>
    /// アイテムをグリッド登録
    /// </summary>
    private void PlaceTreasures()
    {
        int placed = 0;

        PlaceKey();

        while (placed < treasureCount)
        {
            int cx = Random.Range(1, width - 1);
            int cy = Random.Range(1, height - 1);

            if (map[cx, cy] == (int)TileType.Floor && !(map[cx, cy] == (int)TileType.Start) && !(map[cx, cy] == (int)TileType.Goal))
            {
                map[cx, cy] = 100;
                Instantiate(treasurePrefab, new Vector3Int(cx, cy, 0), Quaternion.identity, treasureParent);
                placed++;
            }
        }
    }

    /// <summary>
    /// 鍵をグリッド登録
    /// </summary>
    private void PlaceKey()
    {
        var keyItem = Items[0];

        for (int i = 0; i < Items.Length; i++)
        {
            if (ItemType.Key == Items[i].itemType)
            {
                keyItem = Items[i];
                break;
            }
        }

        int goalArea = GetArea(goalPos);

        List<int> areaCandidates = new List<int> { 0, 1, 2, 3 };
        areaCandidates.Remove(goalArea);

        int keyArea = areaCandidates[Random.Range(0, areaCandidates.Count)];

        List<Vector2Int> candidates = GetAreaFloorTile(keyArea);

        if (candidates.Count > 0)
        {
            var pos = candidates[Random.Range(0, candidates.Count)];
            map[pos.x, pos.y] = 10 + keyItem.ItemID;
            Debug.Log($"ゴール領域:{goalArea} ({goalPos.x}, {goalPos.y})");
            Debug.Log($"{keyItem.ItemName} を 領域の{keyArea} ({pos.x}, {pos.y}) に配置");
        }
        else
        {
            Debug.LogWarning("鍵配置失敗 再計算します");
            PlaceKey();
        }
    }

    /// <summary>
    /// 領域を判定
    /// 左上：0 右上：1 左下：2 右下：3
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    private int GetArea(Vector2Int pos)
    {
        bool left = pos.x < width / 2;
        bool top = pos.y > height / 2;

        if (left && top) return 0;
        if (!left && top) return 1;
        if (left && !top) return 2;
        return 3;
    }

    private List<Vector2Int> GetAreaFloorTile(int area)
    {
        List<Vector2Int> list = new List<Vector2Int>();

        int xStart = 0, xEnd = 0, yStart = 0, yEnd = 0;

        switch (area)
        {
            case 0: // 左上
                xStart = 1; xEnd = width * 1 / 4;
                yStart = height * 3 / 4; yEnd = height - 1;
                break;
            case 1: // 右上
                xStart = width * 3 / 4; xEnd = width - 1;
                yStart = height * 3 / 4; yEnd = height - 1;
                break;
            case 2: // 左下
                xStart = 1; xEnd = width * 1 / 4;
                yStart = 1; yEnd = height * 1 / 4;
                break;
            case 3: // 右下
                xStart = width * 3 / 4; xEnd = width - 1;
                yStart = 1; yEnd = height * 1 / 4;
                break;
        }

        for (int x = xStart; x < xEnd; x++)
        {
            for (int y = yStart; y < yEnd; y++)
            {
                if (map[x, y] == (int)TileType.Floor)
                    list.Add(new Vector2Int(x, y));
            }
        }

        return list;
    }

    // ================== 敵配置 ==================
    private void PlaceEnemies()
    {
        enemyDB = enemyGroup.EnemyGroup;

        if (enemyDB == null || enemyDB.Length == 0)
        {
            Debug.LogError("敵が設定されていません");
            return;
        }

        int placed = 0;

        while (placed < enemyCount)
        {
            int cx = Random.Range(1, width - 1);
            int cy = Random.Range(1, height - 1);

            if (map[cx, cy] == (int)TileType.Floor && !(map[cx, cy] == (int)TileType.Start) && !(map[cx, cy] == (int)TileType.Goal))
            {
                var enemySO = enemyDB[Random.Range(0, enemyDB.Length)];
                Vector3 pos = new Vector3Int(cx, cy, 0);
                GameObject enemy = Instantiate(enemyPrefab, pos, Quaternion.identity, EnemiesParent);
                enemy.GetComponent<EnemyController>().Init(enemySO, new Vector2Int(cx, cy));
                spawnedEnemies.Add(enemy);
                placed++;
            }
        }
    }

    /// <summary>
    /// 敵をすべて削除
    /// </summary>
    private void ClearEnemies()
    {
        foreach (var enemy in spawnedEnemies) Destroy(enemy);
        spawnedEnemies.Clear();
    }

    /// <summary>
    /// 宝箱をすべて削除
    /// </summary>
    private void ClearTreasures()
    {
        foreach (Transform child in treasureParent)
        {
            Destroy(child.gameObject);
        }
    }


    // ================== 描画 ==================
    private void BuildTileMap()
    {
        tilemap.ClearAllTiles();
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                int v = map[x, y];
                if (v == (int)TileType.Void) continue;
                var pos = new Vector3Int(x, y, 0);
                if (v == (int)TileType.Floor)
                {
                    float r = Random.Range(0, 1f);
                    if (r >= 0.1f) tilemap.SetTile(pos, floorTile);
                    else if (r < 0.1f)
                    {
                        int r2 = Random.Range(0, 2);
                        if (r2 == 0) tilemap.SetTile(pos, floorTile2);
                        else if (r2 == 1) tilemap.SetTile(pos, floorTile3);
                    }
                }
                else if (v == (int)TileType.Wall) tilemap.SetTile(pos, wallTile);
                else if (v == (int)TileType.Bedrock) tilemap.SetTile(pos, bedrockTile);
                else if (v == (int)TileType.Start) tilemap.SetTile(pos, startTile);
                else if (v == (int)TileType.Goal) tilemap.SetTile(pos, goalTile);
                else if (v >= 10)
                {
                    int itemID = v - 10;
                    for (int i = 0; i < Items.Length; i++)
                    {
                        var index = Items[i].ItemID;
                        if (index == itemID)
                        {
                            tilemap.SetTile(pos, Items[i].tile);
                            break;
                        }
                    }
                }
            }
    }

    // ================== グリッド取得処理 ==================
    /// <summary>
    /// 移動可能判定処理
    /// </summary>
    /// <param name="pos">移動先</param>
    /// <returns></returns>
    public bool CanMoveTo(Vector3Int pos)
    {
        int cellValue = map[pos.x, pos.y];
        if (cellValue == -1 || cellValue == (int)TileType.Bedrock) return false; // 無効/岩盤は不可
        if (cellValue == (int)TileType.Wall) return HandleWall(pos);
        if (cellValue == (int)TileType.Goal) { HandleGoal(); return false; }

        return true; // それ以外は移動OK
    }

    /// <summary>
    /// プレイヤーが踏んだ処理
    /// </summary>
    /// <param name="pos">踏んだ位置</param>
    public void OnPlayerStep(Vector3Int pos)
    {
        int cellValue = map[pos.x, pos.y];

        // アイテム
        if (cellValue >= 10)
        {
            HandleItem(pos, cellValue);
            return;
        }
    }

    /// <summary>
    /// 壁破壊処理
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    private bool HandleWall(Vector3Int pos)
    {
        if (!wallHits.ContainsKey(pos)) wallHits[pos] = 0;
        wallHits[pos]++;

        if (wallHits[pos] >= wallBreakThreshold)
        {
            map[pos.x, pos.y] = (int)TileType.Floor;
            tilemap.SetTile(pos, floorTile);
        }

        return false;
    }

    /// <summary>
    /// ゴール処理
    /// </summary>
    private void HandleGoal()
    {
        if (inventoryManager.HasKey())
        {
            // TODO: フィールドのレベルアップ処理をここで実行
            Debug.Log("GOAL!");
            inventoryManager.RemoveKey();
            FieldManager.I.AddCountFieldLayer();
            Init();
        }
        else
        {
            // TODO: ポップアップなどでメッセージを表示
            Debug.LogWarning("鍵がありません！");
        }
    }

    /// <summary>
    /// アイテム処理
    /// </summary>
    /// <param name="pos">取得先</param>
    /// <param name="cellValue">取得先のグリッド値</param>
    private void HandleItem(Vector3Int pos, int cellValue)
    {
        int itemID = cellValue - 10;
        for (int i = 0; i < Items.Length; i++)
        {
            if (Items[i].ItemID == itemID)
            {
                inventoryManager.AddItem(Items[i]);
                map[pos.x, pos.y] = (int)TileType.Floor;
                tilemap.SetTile(pos, floorTile);
                break;
            }
        }
    }

}
