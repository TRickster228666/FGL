// HLSL (cs_5_0) для FGL v2:
//  - Иерархический поиск движения (Luma -> Downscale -> Static -> Search -> Subpix -> Median).
//  - Субпиксельный проход параллельный (один блок = одна группа из 64 потоков).
//  - Межпарный вектор PrevV обрабатывается надежно на любом радиусе.
//  - Двунаправленный варп InterpCS с маской перекрытий, защитой от рывков и плавным блендингом.
//  - Формат источников: BGRA (DXGI Desktop Duplication), выход: RGBA.

static class Shaders
{
    public const string Source = """
cbuffer CB : register(b0)
{
    int W;  int H;  int BW; int BH;        // размеры уровня (пиксели) и сетки блоков 8x8
    int PBW; int PBH; int R; int LamMilli; // сетка родителя, радиус поиска, штраф за длину вектора *1000
    int UseParent; int ThreshMilli; int TMilli; int BadMilli; // (для субпикселя UseParent = 1 включает поправку) порог смешивания, момент времени t*1000
    int FlowScaleMilli; int BlendMode; int UseStatic; int UsePrev; // UseStatic: пропуск неподвижных, UsePrev: учитывать вектор прошлой пары
};

int PackV(int2 d)   { return (d.y << 16) | (d.x & 0xFFFF); }
int2 UnpackV(int v) { return int2((v << 16) >> 16, v >> 16); }

// ---------------------------------------------------------------- яркость
Texture2D<float4>        SrcRGBA : register(t0);
RWTexture2D<unorm float> DstLuma : register(u0);

[numthreads(16, 16, 1)]
void LumaCS(uint3 id : SV_DispatchThreadID)
{
    if ((int)id.x >= W || (int)id.y >= H) return;
    float3 c = SrcRGBA.Load(int3(id.xy, 0)).rgb;
    DstLuma[id.xy] = dot(c, float3(0.299, 0.587, 0.114));
}

// ---------------------------------------------------------------- уменьшение вдвое
Texture2D<float>         SrcL : register(t0);
RWTexture2D<unorm float> DstL : register(u0);

[numthreads(16, 16, 1)]
void DownCS(uint3 id : SV_DispatchThreadID)
{
    if ((int)id.x >= W || (int)id.y >= H) return;
    uint sw, sh;
    SrcL.GetDimensions(sw, sh);
    int2 mx = int2((int)sw - 1, (int)sh - 1);
    int2 p = int2(id.xy) * 2;
    float s = SrcL.Load(int3(min(p, mx), 0))
            + SrcL.Load(int3(min(p + int2(1, 0), mx), 0))
            + SrcL.Load(int3(min(p + int2(0, 1), mx), 0))
            + SrcL.Load(int3(min(p + int2(1, 1), mx), 0));
    DstL[id.xy] = s * 0.25;
}

// ---------------------------------------------------------------- какие блоки между A и B не изменились
Texture2D<float>   SA        : register(t0);
Texture2D<float>   SB        : register(t1);
RWTexture2D<uint>  StaticOut : register(u0);

[numthreads(16, 16, 1)]
void StaticCS(uint3 id : SV_DispatchThreadID)
{
    if ((int)id.x >= BW || (int)id.y >= BH) return;
    int2 org = int2(id.xy) * 8;
    int2 lim = int2(W - 1, H - 1);
    float s = 0;      // разница A и B в этой же точке (0 = блок не двигался)
    float grad = 0;   // контраст блока
    [unroll] for (int y = 0; y < 8; y += 2)
    {
        [unroll] for (int x = 0; x < 8; x += 2)
        {
            int2 q = min(org + int2(x, y), lim);
            float a = SA.Load(int3(q, 0));
            s += abs(a - SB.Load(int3(q, 0)));
            grad += abs(a - SA.Load(int3(min(q + int2(1, 0), lim), 0)))
                  + abs(a - SA.Load(int3(min(q + int2(0, 1), lim), 0)));
        }
    }
    StaticOut[id.xy] = (s < 0.12 || grad < 0.35) ? 1u : 0u;
}

// ---------------------------------------------------------------- поиск движения (вперёд: блок в A -> B)
Texture2D<float>   LA      : register(t0);
Texture2D<float>   LB      : register(t1);
Texture2D<int>     ParentV : register(t2);
RWTexture2D<int>   VecOut  : register(u0);
Texture2D<uint>    StaticMap : register(t3);
Texture2D<int>     PrevV   : register(t4);   // векторы предыдущей пары (в 1/32 пикселя)

groupshared float gCost[64];
groupshared int   gIdx[64];

float BlockCost(int2 org, int2 d)
{
    int2 lim = int2(W - 1, H - 1);
    float s = 0;
    [unroll] for (int y = 0; y < 8; y += 2)
    {
        [unroll] for (int x = 0; x < 8; x += 2)
        {
            int2 q1  = min(org + int2(x, y), lim);
            int2 qb1 = clamp(q1 + d, int2(0, 0), lim);
            s += abs(LA.Load(int3(q1, 0)) - LB.Load(int3(qb1, 0)));

            int2 q2  = min(org + int2(x + 1, y + 1), lim);
            int2 qb2 = clamp(q2 + d, int2(0, 0), lim);
            s += abs(LA.Load(int3(q2, 0)) - LB.Load(int3(qb2, 0)));
        }
    }
    return s * 2.0;
}

[numthreads(64, 1, 1)]
void SearchCS(uint3 gid : SV_GroupID, uint tid : SV_GroupIndex)
{
    int2 org = int2(gid.xy) * 8;
    int2 ctr = int2(0, 0);
    if (UseParent != 0)
    {
        int2 pb = min(int2(gid.xy) / 2, int2(PBW - 1, PBH - 1));
        ctr = UnpackV(ParentV.Load(int3(pb, 0))) * 2;
    }

    // блок не изменился между кадрами - движения нет
    if (UseStatic != 0 && StaticMap.Load(int3(gid.xy, 0)) != 0u)
    {
        if (tid == 0) VecOut[gid.xy] = 0;
        return;
    }

    int side  = 2 * R + 1;
    int total = side * side;
    float lam = LamMilli * 0.001;

    float best = 1e30;
    int bestIdx = 0;
    for (int i = (int)tid; i < total; i += 64)
    {
        int2 d = ctr + int2(i % side - R, i / side - R);
        float c = BlockCost(org, d) + lam * (abs(d.x) + abs(d.y));
        if (c < best || (c == best && i < bestIdx)) { best = c; bestIdx = i; }
    }

    // Вектор прошлой пары как альтернативный кандидат (с 4% бонусом).
    // Проверяется потоком 0 надежно независимо от размера total.
    if (UsePrev != 0 && tid == 0)
    {
        int2 d = UnpackV(PrevV.Load(int3(gid.xy, 0)));
        int2 di = int2(round(float2(d) * (1.0 / 32.0)));
        float cPrev = (BlockCost(org, di) + lam * (abs(di.x) + abs(di.y))) * 0.96;
        if (cPrev < best)
        {
            best = cPrev;
            bestIdx = -1; // маркер вектора прошлой пары
        }
    }

    gCost[tid] = best;
    gIdx[tid]  = bestIdx;
    GroupMemoryBarrierWithGroupSync();

    for (uint s = 32; s > 0; s >>= 1)
    {
        if (tid < s)
        {
            float oc = gCost[tid + s];
            int   oi = gIdx[tid + s];
            if (oc < gCost[tid] || (oc == gCost[tid] && oi < gIdx[tid]))
            {
                gCost[tid] = oc;
                gIdx[tid]  = oi;
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (tid == 0)
    {
        int i = gIdx[0];
        if (i == -1) VecOut[gid.xy] = PrevV.Load(int3(gid.xy, 0));   // победил вектор прошлой пары
        else VecOut[gid.xy] = PackV(ctr + int2(i % side - R, i / side - R));
    }
}

// ---------------------------------------------------------------- субпиксельное уточнение (1/32 px)
groupshared float gS[320];

[numthreads(64, 1, 1)]
void SubpixCS(uint3 gid : SV_GroupID, uint tid : SV_GroupIndex)
{
    int2 lim = int2(W - 1, H - 1);
    if (UseStatic != 0 && StaticMap.Load(int3(gid.xy, 0)) != 0u)
    {
        if (tid == 0) VecOut[gid.xy] = 0;
        return;
    }
    int2 d = UnpackV(ParentV.Load(int3(gid.xy, 0)));
    int2 q = min(int2(gid.xy) * 8 + int2((int)(tid & 7u), (int)(tid >> 3u)), lim);
    float a = LA.Load(int3(q, 0));
    [unroll] for (int k = 0; k < 5; k++)
    {
        int2 o = int2(0, 0);
        if (k == 1) o = int2(-1, 0);
        if (k == 2) o = int2(1, 0);
        if (k == 3) o = int2(0, -1);
        if (k == 4) o = int2(0, 1);
        gS[k * 64 + (int)tid] = abs(a - LB.Load(int3(clamp(q + d + o, int2(0, 0), lim), 0)));
    }
    GroupMemoryBarrierWithGroupSync();

    for (uint s = 32; s > 0; s >>= 1)
    {
        if (tid < s)
        {
            [unroll] for (int k2 = 0; k2 < 5; k2++)
                gS[k2 * 64 + (int)tid] += gS[k2 * 64 + (int)tid + (int)s];
        }
        GroupMemoryBarrierWithGroupSync();
    }

    if (tid == 0)
    {
        int2 off32 = int2(0, 0);
        if (UseParent != 0)
        {
            float c0  = gS[0];
            float cxm = gS[64];
            float cxp = gS[128];
            float cym = gS[192];
            float cyp = gS[256];
            float2 off = float2(0.0, 0.0);
            float qx = cxm - 2.0 * c0 + cxp;
            float qy = cym - 2.0 * c0 + cyp;
            if (qx > 0.001) off.x = clamp(0.5 * (cxm - cxp) / qx, -0.5, 0.5);
            if (qy > 0.001) off.y = clamp(0.5 * (cym - cyp) / qy, -0.5, 0.5);
            off32 = int2(round(off * 32.0));
        }
        VecOut[gid.xy] = PackV(d * 32 + off32);
    }
}

// ---------------------------------------------------------------- медиана векторов 3x3
Texture2D<int>     MedIn  : register(t0);
RWTexture2D<int>   MedOut : register(u0);

int Median9(int a[9])
{
    for (int i = 0; i < 9; i++)
    {
        int lt = 0;
        int le = 0;
        for (int j = 0; j < 9; j++)
        {
            lt += (a[j] <  a[i]) ? 1 : 0;
            le += (a[j] <= a[i]) ? 1 : 0;
        }
        if (lt <= 4 && le >= 5) return a[i];
    }
    return a[4];
}

[numthreads(16, 16, 1)]
void MedianCS(uint3 id : SV_DispatchThreadID)
{
    if ((int)id.x >= BW || (int)id.y >= BH) return;
    int xs[9];
    int ys[9];
    int n = 0;
    for (int dy = -1; dy <= 1; dy++)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            int2 q = clamp(int2(id.xy) + int2(dx, dy), int2(0, 0), int2(BW - 1, BH - 1));
            int2 v = UnpackV(MedIn.Load(int3(q, 0)));
            xs[n] = v.x;
            ys[n] = v.y;
            n++;
        }
    }
    MedOut[id.xy] = PackV(int2(Median9(xs), Median9(ys)));
}

// ---------------------------------------------------------------- проверка разницы кадров
Texture2D<float4>   DA      : register(t0);   // кандидат
Texture2D<float4>   DB      : register(t1);   // текущий кадр B
RWTexture2D<uint>   DiffOut : register(u0);

groupshared uint gD[256];

[numthreads(256, 1, 1)]
void DiffCS(uint tid : SV_GroupIndex)
{
    int gx = (W + 7) / 8;
    int gy = (H + 7) / 8;
    int total = gx * gy;
    uint acc = 0;
    for (int i = (int)tid; i < total; i += 256)
    {
        int2 p = int2((i % gx) * 8 + 3, (i / gx) * 8 + 3);
        p = min(p, int2(W - 1, H - 1));
        float3 d = abs(DA.Load(int3(p, 0)).rgb - DB.Load(int3(p, 0)).rgb);
        acc += (uint)(dot(d, float3(1, 1, 1)) * 255.0 + 0.5);
    }
    gD[tid] = acc;
    GroupMemoryBarrierWithGroupSync();

    for (uint s = 128; s > 0; s >>= 1)
    {
        if (tid < s) gD[tid] += gD[tid + s];
        GroupMemoryBarrierWithGroupSync();
    }
    if (tid == 0) DiffOut[uint2(0, 0)] = gD[0];
}

// ---------------------------------------------------------------- построение кадра в момент t (0=A, 1=B, >1=экстраполяция)
Texture2D<float4>         FA      : register(t0);
Texture2D<float4>         FB      : register(t1);
Texture2D<int>            V0      : register(t2);
Texture2D<int>            VB      : register(t5);   // обратный поток на уровне 1
Texture2D<int>            VF1     : register(t6);   // прямой поток на уровне 1
SamplerState              Smp     : register(s0);
RWTexture2D<unorm float4> OutImg  : register(u0);

int2 VLoad(int2 b)
{
    b = clamp(b, int2(0, 0), int2(BW - 1, BH - 1));
    return UnpackV(V0.Load(int3(b, 0)));
}

float2 VecAt(float2 p)
{
    float2 g = p * (1.0 / 8.0) - 0.5;
    int2   b = int2(floor(g));
    float2 f = g - floor(g);
    float2 v00 = float2(VLoad(b));
    float2 v10 = float2(VLoad(b + int2(1, 0)));
    float2 v01 = float2(VLoad(b + int2(0, 1)));
    float2 v11 = float2(VLoad(b + int2(1, 1)));
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y) * (1.0 / 32.0);
}

int2 B1Size() { return int2((((W >> 1) + 7) / 8) - 1, (((H >> 1) + 7) / 8) - 1); }

float2 VecBAt(float2 p)
{
    int2 nb = B1Size();
    float2 g = p * (1.0 / 16.0) - 0.5;
    int2   b = int2(floor(g));
    float2 f = g - floor(g);
    float2 v00 = float2(UnpackV(VB.Load(int3(clamp(b,                int2(0, 0), nb), 0))));
    float2 v10 = float2(UnpackV(VB.Load(int3(clamp(b + int2(1, 0), int2(0, 0), nb), 0))));
    float2 v01 = float2(UnpackV(VB.Load(int3(clamp(b + int2(0, 1), int2(0, 0), nb), 0))));
    float2 v11 = float2(UnpackV(VB.Load(int3(clamp(b + int2(1, 1), int2(0, 0), nb), 0))));
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

float2 VecF1At(float2 p)
{
    int2 nb = B1Size();
    float2 g = p * (1.0 / 16.0) - 0.5;
    int2   b = int2(floor(g));
    float2 f = g - floor(g);
    float2 v00 = float2(UnpackV(VF1.Load(int3(clamp(b,                int2(0, 0), nb), 0))));
    float2 v10 = float2(UnpackV(VF1.Load(int3(clamp(b + int2(1, 0), int2(0, 0), nb), 0))));
    float2 v01 = float2(UnpackV(VF1.Load(int3(clamp(b + int2(0, 1), int2(0, 0), nb), 0))));
    float2 v11 = float2(UnpackV(VF1.Load(int3(clamp(b + int2(1, 1), int2(0, 0), nb), 0))));
    return lerp(lerp(v00, v10, f.x), lerp(v01, v11, f.x), f.y);
}

[numthreads(16, 16, 1)]
void InterpCS(uint3 id : SV_DispatchThreadID)
{
    if ((int)id.x >= W || (int)id.y >= H) return;

    float  t   = TMilli * 0.001;
    float2 p   = float2(id.xy) + 0.5;
    float2 inv = 1.0 / float2(W, H);

    float4 az = FA.SampleLevel(Smp, p * inv, 0);
    float4 bz = FB.SampleLevel(Smp, p * inv, 0);

    if (BlendMode == 1)
    {
        OutImg[id.xy] = float4(lerp(az.rgb, bz.rgb, saturate(t)), 1.0);
        return;
    }
    bool allowFade = (BlendMode != 2);   // BlendMode 2 = чистый варп

    // статика: пиксели A и B одинаковы
    float ez = dot(abs(az.rgb - bz.rgb), float3(1, 1, 1));
    if (ez < 0.01) { OutImg[id.xy] = float4(az.rgb, 1.0); return; }

    // за пределами B движение досчитывается вперёд (экстраполяция с демпфированием)
    float damp = t > 1.0 ? saturate(1.0 - 0.35 * (t - 1.0)) : 1.0;
    float  w = saturate(t);

    float2 d0 = VecAt(p) * damp;
    float4 a0 = FA.SampleLevel(Smp, (p - t * d0) * inv, 0);
    float4 b0 = FB.SampleLevel(Smp, (p + (1.0 - t) * d0) * inv, 0);
    float  e0 = dot(abs(a0.rgb - b0.rgb), float3(1, 1, 1));
    float3 c0 = lerp(a0.rgb, b0.rgb, w);

    float3 col;
    float  eb;

    // Адаптивный быстрый путь: в областях ровного движения (где расхождение e0 < 0.05)
    // вектор d0 уже точен — пропускаем расчет d1/d2 и 4 выборки текстур
    if (e0 < 0.05)
    {
        col = c0;
        eb = e0;
    }
    else
    {
        float2 d1 = VecAt(p - t * d0) * damp;
        float2 d2 = VecAt(p + (1.0 - t) * d0) * damp;

        float4 a1 = FA.SampleLevel(Smp, (p - t * d1) * inv, 0);
        float4 b1 = FB.SampleLevel(Smp, (p + (1.0 - t) * d1) * inv, 0);
        float  e1 = dot(abs(a1.rgb - b1.rgb), float3(1, 1, 1));
        float3 c1 = lerp(a1.rgb, b1.rgb, w);

        float4 a2 = FA.SampleLevel(Smp, (p - t * d2) * inv, 0);
        float4 b2 = FB.SampleLevel(Smp, (p + (1.0 - t) * d2) * inv, 0);
        float  e2 = dot(abs(a2.rgb - b2.rgb), float3(1, 1, 1));
        float3 c2 = lerp(a2.rgb, b2.rgb, w);

        eb = min(e0, min(e1, e2));
        float g  = 1.0 / 0.02;
        float k0 = saturate(1.0 - (e0 - eb) * g);
        float k1 = saturate(1.0 - (e1 - eb) * g);
        float k2 = saturate(1.0 - (e2 - eb) * g);
        col = (c0 * k0 + c1 * k1 + c2 * k2) / max(k0 + k1 + k2, 1e-3);
    }

    float3 safe = lerp(az.rgb, bz.rgb, saturate(t));

    // расхождение векторов: смягчаем в смешивание только при сильном несоответствии
    float bad = saturate((eb - 0.55) * 4.0);
    if (allowFade) col = lerp(col, safe, bad * 0.5);

    // маска перекрытий (cross-check прямой и обратный)
    if (BadMilli != 0)
    {
        float2 dF1  = VecF1At(p) * (1.0 / 32.0);
        float2 dB1  = VecBAt(p + dF1) * (1.0 / 32.0);
        float  cons = length(dB1 + dF1);
        float  occ  = saturate((cons - 1.5) * 0.6);
        if (allowFade) col = lerp(col, safe, occ * 0.5);
    }

    // подмешивание у самых границ (t в пределах 2% от 0 или 1) для устранения стыков пар
    float edge  = min(t, 1.0 - t);
    float wSafe = saturate(1.0 - edge * (1.0 / 0.02));
    if (allowFade) col = lerp(col, safe, wSafe);

    OutImg[id.xy] = float4(col, 1.0);
}
""";
}
