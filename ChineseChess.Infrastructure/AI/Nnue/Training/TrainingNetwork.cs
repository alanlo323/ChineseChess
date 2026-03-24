using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;
using ChineseChess.Infrastructure.AI.Nnue.Helpers;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// float32 訓練版網路：前向傳播 + WDL 損失反向傳播 + 量化感知訓練（QAT）。
///
/// 網路結構與推論版（NnueNetwork）完全對應：
///   FT  → pairwise_mul_clamp → FC0 → SqrCReLU+CReLU → FC1 → CReLU → FC2 → score
///
/// QAT 原則：訓練中將 int8 層的權重 clamp 至 [-127, 127]，
/// 使模型在訓練時就適應量化誤差。
/// </summary>
public sealed class TrainingNetwork
{
    // ── 推論常數（對應 NnueNetwork）────────────────────────────────────
    private const int L1          = NnueFileFormat.HalfDimensions;   // 1024
    private const int L2          = NnueFileFormat.L2;               // 15
    private const int L3          = NnueFileFormat.L3;               // 32
    private const int PsqtBuckets = NnueFileFormat.PsqtBuckets;      // 16
    private const int Dims        = NnueFileFormat.Dimensions;        // 16536
    private const int Stacks      = NnueFileFormat.LayerStacksNb;     // 16

    // int8/int16 量化範圍（QAT clamp）
    private const float Int8Max  = 127f;
    private const float Int16Max = 32767f;

    // OutputScale（對應推論版）
    private const float OutputScale          = 16f;
    private const float WeightScaleBitsF     = 64f;   // 1 << 6

    // 命名常數（避免魔術數字散落各處）
    private const float PairwiseClampDivisor = 512f;
    private const float PairwiseClampMax     = 255f;
    private const float WdlScaleFactor       = 600f;
    private const float SqrCReLUExtraDivisor = 128f;
    private const float FtBiasScale          = 2f;
    private const float LogEpsilon           = 1e-7f;
    private const float PsqtHalfScale        = 0.5f;

    // ── 訓練參數 ─────────────────────────────────────────────────────
    public float[] FtBiases  { get; }  // float[1024]
    public float[] FtWeights { get; }  // float[Dims * 1024]
    public float[] FtPsqt    { get; }  // float[Dims * 16]

    public float[][] Fc0Biases  { get; }  // [16][L2+1]
    public float[][] Fc0Weights { get; }  // [16][(L2+1)*1024]
    public float[][] Fc1Biases  { get; }  // [16][L3]
    public float[][] Fc1Weights { get; }  // [16][L3*32]
    public float[][] Fc2Biases  { get; }  // [16][1]
    public float[][] Fc2Weights { get; }  // [16][32]

    // ── 前向快取（ForwardAndLoss → Backward 共用）────────────────────────
    private int      cachedStackBucket;
    private int      cachedUsPerspColorIdx;
    private float[,] cachedFtAcc  = new float[2, L1];
    private float[]  cachedFtOut  = new float[L1];
    private float[]  cachedFc0Out = new float[L2 + 1];
    private float[]  cachedAc0    = new float[32];
    private float[]  cachedFc1Out = new float[L3];
    private float[]  cachedAc1    = new float[L3];
    private float    cachedSigPred;
    private bool     cachedValuesValid;   // 防止 Backward() 在 ForwardAndLoss() 前被呼叫
    private int      cachedFeatCount0;    // ComputeFtAccumulator 快取特徵數（perspective 0）
    private int      cachedFeatCount1;    // ComputeFtAccumulator 快取特徵數（perspective 1）
    private readonly int[] cachedFeats0 = new int[32];  // perspective 0 特徵索引快取
    private readonly int[] cachedFeats1 = new int[32];  // perspective 1 特徵索引快取

    // ── 反向傳播工作緩衝（預分配，避免每次 Backward 呼叫觸發 GC 配置）────
    private readonly float[]  bwdDAc1     = new float[L3];
    private readonly float[]  bwdDFc1Out  = new float[L3];
    private readonly float[]  bwdDAc0     = new float[32];
    private readonly float[]  bwdDFc0Out  = new float[L2];
    private readonly float[]  bwdDFtOut   = new float[L1];
    private readonly float[,] bwdDFtAcc   = new float[2, L1];
    private readonly int[]    featuresWork = new int[32];  // ForwardAndLoss + Backward 共用

    // ── 梯度（對應同形狀）─────────────────────────────────────────────
    private readonly float[] gradFtBiases;
    private readonly float[] gradFtWeights;
    private readonly float[] gradFtPsqt;
    private readonly float[][] gradFc0Biases;
    private readonly float[][] gradFc0Weights;
    private readonly float[][] gradFc1Biases;
    private readonly float[][] gradFc1Weights;
    private readonly float[][] gradFc2Biases;
    private readonly float[][] gradFc2Weights;

    // ── Adam 狀態（m / v 一次動量 / 二次動量）────────────────────────
    private readonly float[] m_FtBiases,   v_FtBiases;
    private readonly float[] m_FtWeights,  v_FtWeights;
    private readonly float[] m_FtPsqt,     v_FtPsqt;
    private readonly float[][] m_Fc0Biases, v_Fc0Biases;
    private readonly float[][] m_Fc0Weights,v_Fc0Weights;
    private readonly float[][] m_Fc1Biases, v_Fc1Biases;
    private readonly float[][] m_Fc1Weights,v_Fc1Weights;
    private readonly float[][] m_Fc2Biases, v_Fc2Biases;
    private readonly float[][] m_Fc2Weights,v_Fc2Weights;

    /// <summary>已執行的 Adam 更新步數（用於偏差校正）。</summary>
    public long AdamStepCount { get; private set; }

    public TrainingNetwork()
    {
        FtBiases  = new float[L1];
        FtWeights = new float[(long)Dims * L1];
        FtPsqt    = new float[(long)Dims * PsqtBuckets];

        Fc0Biases  = new float[Stacks][];
        Fc0Weights = new float[Stacks][];
        Fc1Biases  = new float[Stacks][];
        Fc1Weights = new float[Stacks][];
        Fc2Biases  = new float[Stacks][];
        Fc2Weights = new float[Stacks][];

        for (int s = 0; s < Stacks; s++)
        {
            Fc0Biases[s]  = new float[L2 + 1];
            Fc0Weights[s] = new float[(L2 + 1) * L1];
            Fc1Biases[s]  = new float[L3];
            Fc1Weights[s] = new float[L3 * 32];
            Fc2Biases[s]  = new float[1];
            Fc2Weights[s] = new float[32];
        }

        // 梯度陣列（同形狀）
        gradFtBiases  = new float[L1];
        gradFtWeights = new float[(long)Dims * L1];
        gradFtPsqt    = new float[(long)Dims * PsqtBuckets];

        gradFc0Biases  = AllocJagged(Stacks, s => Fc0Biases[s].Length);
        gradFc0Weights = AllocJagged(Stacks, s => Fc0Weights[s].Length);
        gradFc1Biases  = AllocJagged(Stacks, s => Fc1Biases[s].Length);
        gradFc1Weights = AllocJagged(Stacks, s => Fc1Weights[s].Length);
        gradFc2Biases  = AllocJagged(Stacks, s => Fc2Biases[s].Length);
        gradFc2Weights = AllocJagged(Stacks, s => Fc2Weights[s].Length);

        // Adam 狀態（同形狀）
        m_FtBiases  = new float[L1];   v_FtBiases  = new float[L1];
        m_FtWeights = new float[(long)Dims * L1];
        v_FtWeights = new float[(long)Dims * L1];
        m_FtPsqt    = new float[(long)Dims * PsqtBuckets];
        v_FtPsqt    = new float[(long)Dims * PsqtBuckets];

        m_Fc0Biases  = AllocJagged(Stacks, s => Fc0Biases[s].Length);
        v_Fc0Biases  = AllocJagged(Stacks, s => Fc0Biases[s].Length);
        m_Fc0Weights = AllocJagged(Stacks, s => Fc0Weights[s].Length);
        v_Fc0Weights = AllocJagged(Stacks, s => Fc0Weights[s].Length);
        m_Fc1Biases  = AllocJagged(Stacks, s => Fc1Biases[s].Length);
        v_Fc1Biases  = AllocJagged(Stacks, s => Fc1Biases[s].Length);
        m_Fc1Weights = AllocJagged(Stacks, s => Fc1Weights[s].Length);
        v_Fc1Weights = AllocJagged(Stacks, s => Fc1Weights[s].Length);
        m_Fc2Biases  = AllocJagged(Stacks, s => Fc2Biases[s].Length);
        v_Fc2Biases  = AllocJagged(Stacks, s => Fc2Biases[s].Length);
        m_Fc2Weights = AllocJagged(Stacks, s => Fc2Weights[s].Length);
        v_Fc2Weights = AllocJagged(Stacks, s => Fc2Weights[s].Length);

        InitWeights();
    }

    // ── 初始化 ───────────────────────────────────────────────────────

    private void InitWeights()
    {
        var rng = new Random(42);

        // FT biases：0；FT weights：Xavier 均勻分佈
        float ftScale = MathF.Sqrt(6f / (Dims + L1));
        for (long i = 0; i < FtWeights.LongLength; i++)
            FtWeights[i] = (float)(rng.NextDouble() * 2 - 1) * ftScale;

        for (int s = 0; s < Stacks; s++)
        {
            InitLayer(Fc0Weights[s], L1,     L2 + 1, rng);
            InitLayer(Fc1Weights[s], L2 * 2, L3,     rng);
            InitLayer(Fc2Weights[s], L3,     1,      rng);
        }
    }

    private static void InitLayer(float[] weights, int inDims, int outDims, Random rng)
    {
        float scale = MathF.Sqrt(6f / (inDims + outDims));
        for (int i = 0; i < weights.Length; i++)
            weights[i] = (float)(rng.NextDouble() * 2 - 1) * scale;
    }

    // ── 從已載入的量化權重初始化（Fine-tune 用）────────────────────────

    public void LoadFromQuantized(NnueWeights q)
    {
        // FT biases（int16 → float，除以 FtBiasScale 還原縮放）
        for (int i = 0; i < L1; i++)
            FtBiases[i] = q.FtBiases[i] / FtBiasScale;

        // FT weights（int8 → float）
        for (long i = 0; i < q.FtWeights.LongLength; i++)
            FtWeights[i] = q.FtWeights[i];

        // FT PSQT（int32 → float）
        for (long i = 0; i < q.FtPsqtWeights.LongLength; i++)
            FtPsqt[i] = q.FtPsqtWeights[i] / WeightScaleBitsF;

        for (int s = 0; s < Stacks; s++)
        {
            var st = q.Stacks[s];
            for (int i = 0; i < st.Fc0Biases.Length; i++)
                Fc0Biases[s][i] = st.Fc0Biases[i] / WeightScaleBitsF;
            for (int i = 0; i < st.Fc0Weights.Length; i++)
                Fc0Weights[s][i] = st.Fc0Weights[i];
            for (int i = 0; i < st.Fc1Biases.Length; i++)
                Fc1Biases[s][i] = st.Fc1Biases[i] / WeightScaleBitsF;
            for (int i = 0; i < st.Fc1Weights.Length; i++)
                Fc1Weights[s][i] = st.Fc1Weights[i];
            for (int i = 0; i < st.Fc2Biases.Length; i++)
                Fc2Biases[s][i] = st.Fc2Biases[i] / WeightScaleBitsF;
            for (int i = 0; i < st.Fc2Weights.Length; i++)
                Fc2Weights[s][i] = st.Fc2Weights[i];
        }
    }

    // ── 前向傳播 ─────────────────────────────────────────────────────

    /// <summary>
    /// 計算指定局面的預測分數並回傳 WDL 損失。
    /// 同時填入用於反向傳播的中間值。
    /// </summary>
    public float ForwardAndLoss(
        IBoard board,
        float targetResult,
        out float predictedScore)
    {
        int usPerspColorIdx = cachedUsPerspColorIdx = board.Turn == PieceColor.Red ? 0 : 1;
        int stackBucket = cachedStackBucket = LayerStackBucketHelper.GetBucket(board);

        // FT：就地計算雙視角累加器（重用 cachedFtAcc，無堆積配置）
        ComputeFtAccumulator(board, cachedFtAcc);
        var ftAcc = cachedFtAcc;

        // Transform：pairwise clamp ReLU → uint8-range float（直接寫入快取陣列）
        var ftOut = cachedFtOut;
        for (int p = 0; p < 2; p++)
        {
            int colorIdx  = p == 0 ? usPerspColorIdx : 1 - usPerspColorIdx;
            int outOffset = p * (L1 / 2);
            for (int j = 0; j < L1 / 2; j++)
            {
                float a0 = Math.Clamp(ftAcc[colorIdx, j],          0f, PairwiseClampMax);
                float a1 = Math.Clamp(ftAcc[colorIdx, j + L1 / 2], 0f, PairwiseClampMax);
                ftOut[outOffset + j] = a0 * a1 / PairwiseClampDivisor;
            }
        }

        // FC0（直接寫入快取陣列）
        var fc0Out = cachedFc0Out;
        for (int j = 0; j <= L2; j++)
        {
            float sum = Fc0Biases[stackBucket][j];
            for (int i = 0; i < L1; i++)
                sum += Fc0Weights[stackBucket][i + j * L1] * ftOut[i];
            fc0Out[j] = sum;
        }

        // PSQT 分量（fc0Out[L2]）
        float psqtFrac = fc0Out[L2] * WdlScaleFactor * OutputScale / (Int8Max * WeightScaleBitsF);

        // SqrCReLU + CReLU → ac0（直接寫入快取陣列）
        var ac0 = cachedAc0;
        for (int i = 0; i < L2; i++)
        {
            float v = fc0Out[i];
            ac0[i]      = Math.Clamp(v * v / (WeightScaleBitsF * WeightScaleBitsF * SqrCReLUExtraDivisor), 0f, Int8Max);  // SqrCReLU
            ac0[i + L2] = Math.Clamp(v / WeightScaleBitsF, 0f, Int8Max);  // CReLU
        }

        // FC1（直接寫入快取陣列）
        var fc1Out = cachedFc1Out;
        for (int j = 0; j < L3; j++)
        {
            float sum = Fc1Biases[stackBucket][j];
            for (int i = 0; i < 32; i++)
                sum += Fc1Weights[stackBucket][i + j * 32] * ac0[i];
            fc1Out[j] = sum;
        }

        // CReLU → ac1（直接寫入快取陣列）
        var ac1 = cachedAc1;
        for (int i = 0; i < L3; i++)
            ac1[i] = Math.Clamp(fc1Out[i] / WeightScaleBitsF, 0f, Int8Max);

        // FC2
        float fc2Out = Fc2Biases[stackBucket][0];
        for (int i = 0; i < L3; i++)
            fc2Out += Fc2Weights[stackBucket][i] * ac1[i];

        // PSQT
        float psqt = ComputePsqt(usPerspColorIdx, stackBucket);

        // 合併分數
        float positional = (fc2Out + psqtFrac) / OutputScale;
        predictedScore = psqt / OutputScale + positional;

        // WDL 損失：cross-entropy with sigmoid
        cachedSigPred = Sigmoid(predictedScore / WdlScaleFactor);
        cachedValuesValid = true;
        float sigPred = cachedSigPred;
        float loss = -(targetResult * MathF.Log(sigPred + LogEpsilon)
                     + (1f - targetResult) * MathF.Log(1f - sigPred + LogEpsilon));

        return loss;
    }

    // ── 反向傳播 ─────────────────────────────────────────────────────

    /// <summary>計算梯度並累積至 grad* 陣列（呼叫 Adam 步驟前不清除）。</summary>
    /// <exception cref="InvalidOperationException">未先呼叫 ForwardAndLoss() 時拋出。</exception>
    public void Backward(IBoard board, float targetResult, float batchSize = 1f)
    {
        if (!cachedValuesValid)
            throw new InvalidOperationException("必須先呼叫 ForwardAndLoss() 才能呼叫 Backward()。");

        int sb = cachedStackBucket;

        // ── A：dL/d(predictedScore)（sigmoid × cross-entropy 偏微分相消）────
        float dScore = (cachedSigPred - targetResult) / (WdlScaleFactor * batchSize);

        // ── B：FC2 梯度（使用預分配工作緩衝 bwdDAc1）────────────────────────
        float dFc2Out = dScore / OutputScale;
        gradFc2Biases[sb][0] += dFc2Out;
        var dAc1 = bwdDAc1;
        for (int i = 0; i < L3; i++)
        {
            gradFc2Weights[sb][i] += dFc2Out * cachedAc1[i];
            dAc1[i] = dFc2Out * Fc2Weights[sb][i];
        }

        // ── C：CReLU backward（fc1Out → ac1）────────────────────────────────
        var dFc1Out = bwdDFc1Out;
        for (int i = 0; i < L3; i++)
            dFc1Out[i] = (cachedFc1Out[i] > 0f && cachedAc1[i] < Int8Max)
                         ? dAc1[i] / WeightScaleBitsF : 0f;

        // ── D：FC1 梯度（bwdDAc0 需清零，因為下方為累積寫入）────────────────
        var dAc0 = bwdDAc0;
        Array.Clear(dAc0);
        for (int j = 0; j < L3; j++)
        {
            gradFc1Biases[sb][j] += dFc1Out[j];
            for (int i = 0; i < 32; i++)
            {
                gradFc1Weights[sb][i + j * 32] += dFc1Out[j] * cachedAc0[i];
                dAc0[i] += dFc1Out[j] * Fc1Weights[sb][i + j * 32];
            }
        }

        // ── E：SqrCReLU + CReLU backward（fc0Out[0..L2) → ac0）────────────
        var dFc0Out = bwdDFc0Out;
        for (int i = 0; i < L2; i++)
        {
            float v = cachedFc0Out[i];
            // SqrCReLU：d(clamp(v²/D))/dv = 2v/D（非飽和區間）
            float dSqr = (cachedAc0[i] > 0f && cachedAc0[i] < Int8Max)
                         ? dAc0[i] * 2f * v / (WeightScaleBitsF * WeightScaleBitsF * SqrCReLUExtraDivisor) : 0f;
            // CReLU：d(clamp(v/64))/dv = 1/64（非飽和區間）
            float dCre = (cachedAc0[i + L2] > 0f && cachedAc0[i + L2] < Int8Max)
                         ? dAc0[i + L2] / WeightScaleBitsF : 0f;
            dFc0Out[i] = dSqr + dCre;
        }

        // ── F：PSQT neuron（fc0Out[L2]）梯度 ─────────────────────────────────
        // predictedScore 中含 fc0Out[L2] * WdlScaleFactor / (Int8Max * 64) 分量
        float dFc0PsqtNeuron = dScore * WdlScaleFactor / (Int8Max * WeightScaleBitsF);

        // ── G：FC0 梯度（含 PSQT neuron），同時累積 dFtOut ─────────────────
        var dFtOut = bwdDFtOut;
        Array.Clear(dFtOut);
        for (int j = 0; j < L2; j++)
        {
            gradFc0Biases[sb][j] += dFc0Out[j];
            for (int i = 0; i < L1; i++)
            {
                gradFc0Weights[sb][i + j * L1] += dFc0Out[j] * cachedFtOut[i];
                dFtOut[i] += dFc0Out[j] * Fc0Weights[sb][i + j * L1];
            }
        }
        // PSQT neuron 欄（j = L2）
        gradFc0Biases[sb][L2] += dFc0PsqtNeuron;
        for (int i = 0; i < L1; i++)
        {
            gradFc0Weights[sb][i + L2 * L1] += dFc0PsqtNeuron * cachedFtOut[i];
            dFtOut[i] += dFc0PsqtNeuron * Fc0Weights[sb][i + L2 * L1];
        }

        // ── H：Pairwise mul-clamp backward（ftAcc → ftOut）──────────────────
        // ftOut[p*H+j] = clamp(a0) × clamp(a1) / PairwiseClampDivisor
        // dFtAcc[ci,j]   += clamp(a1)/D × dg  當 a0 ∈ (0,255)
        // dFtAcc[ci,j+H] += clamp(a0)/D × dg  當 a1 ∈ (0,255)
        int H = L1 / 2;
        var dFtAcc = bwdDFtAcc;
        Array.Clear(dFtAcc);
        for (int p = 0; p < 2; p++)
        {
            int ci = p == 0 ? cachedUsPerspColorIdx : 1 - cachedUsPerspColorIdx;
            for (int j = 0; j < H; j++)
            {
                float rawA0 = cachedFtAcc[ci, j];
                float rawA1 = cachedFtAcc[ci, j + H];
                float a0 = Math.Clamp(rawA0, 0f, PairwiseClampMax);
                float a1 = Math.Clamp(rawA1, 0f, PairwiseClampMax);
                float dg = dFtOut[p * H + j];
                if (rawA0 > 0f && rawA0 < PairwiseClampMax) dFtAcc[ci, j]     += a1 / PairwiseClampDivisor * dg;
                if (rawA1 > 0f && rawA1 < PairwiseClampMax) dFtAcc[ci, j + H] += a0 / PairwiseClampDivisor * dg;
            }
        }

        // ── I：FT Sparse 梯度（Biases ×2、Weights 稀疏、PSQT 稀疏）──────────
        // 重用 ComputeFtAccumulator 已快取的特徵索引，無需再次呼叫 GetActiveFeatures
        for (int c = 0; c < 2; c++)
        {
            int   count = c == 0 ? cachedFeatCount0 : cachedFeatCount1;
            int[] feats = c == 0 ? cachedFeats0 : cachedFeats1;

            // Biases：acc[c,n] = FtBiases[n] * FtBiasScale → 梯度需 ×FtBiasScale
            for (int n = 0; n < L1; n++)
                gradFtBiases[n] += dFtAcc[c, n] * FtBiasScale;

            // Weights（稀疏：只更新活躍特徵的對應行）
            for (int f = 0; f < count; f++)
            {
                long wOff = (long)feats[f] * L1;
                for (int n = 0; n < L1; n++)
                    gradFtWeights[wOff + n] += dFtAcc[c, n];
            }

            // PSQT：psqt = (usSum - themSum) / 2 → d(psqt/OutputScale)/d(sum) = ±0.5/OutputScale
            float sign = (c == cachedUsPerspColorIdx) ? 1f : -1f;
            float dPsqtPerFeature = dScore * sign * PsqtHalfScale / OutputScale;
            for (int f = 0; f < count; f++)
            {
                long pOff = (long)feats[f] * PsqtBuckets + cachedStackBucket;
                gradFtPsqt[pOff] += dPsqtPerFeature;
            }
        }
    }

    /// <summary>清除所有梯度陣列（每批次開始時呼叫）。</summary>
    public void ZeroGradients()
    {
        Array.Clear(gradFtBiases);
        Array.Clear(gradFtWeights);
        Array.Clear(gradFtPsqt);
        for (int s = 0; s < Stacks; s++)
        {
            Array.Clear(gradFc0Biases[s]);
            Array.Clear(gradFc0Weights[s]);
            Array.Clear(gradFc1Biases[s]);
            Array.Clear(gradFc1Weights[s]);
            Array.Clear(gradFc2Biases[s]);
            Array.Clear(gradFc2Weights[s]);
        }
    }

    // ── Adam 優化器更新步驟 ──────────────────────────────────────────

    /// <summary>執行一步 Adam 更新（β1=0.9, β2=0.999, ε=1e-8）。</summary>
    public void StepAdam(float lr, float beta1 = 0.9f, float beta2 = 0.999f, float eps = 1e-8f)
    {
        AdamStepCount++;
        float bc1 = 1f - MathF.Pow(beta1, AdamStepCount);
        float bc2 = 1f - MathF.Pow(beta2, AdamStepCount);
        float lrCorrected = lr * MathF.Sqrt(bc2) / bc1;

        AdamUpdate(FtBiases,  gradFtBiases,  m_FtBiases,  v_FtBiases,  lrCorrected, beta1, beta2, eps, Int16Max);
        AdamUpdate(FtWeights, gradFtWeights, m_FtWeights, v_FtWeights, lrCorrected, beta1, beta2, eps, Int8Max);
        AdamUpdate(FtPsqt,    gradFtPsqt,    m_FtPsqt,    v_FtPsqt,    lrCorrected, beta1, beta2, eps, float.MaxValue);

        for (int s = 0; s < Stacks; s++)
        {
            AdamUpdate(Fc0Biases[s],  gradFc0Biases[s],  m_Fc0Biases[s],  v_Fc0Biases[s],  lrCorrected, beta1, beta2, eps, float.MaxValue);
            AdamUpdate(Fc0Weights[s], gradFc0Weights[s], m_Fc0Weights[s], v_Fc0Weights[s], lrCorrected, beta1, beta2, eps, Int8Max);
            AdamUpdate(Fc1Biases[s],  gradFc1Biases[s],  m_Fc1Biases[s],  v_Fc1Biases[s],  lrCorrected, beta1, beta2, eps, float.MaxValue);
            AdamUpdate(Fc1Weights[s], gradFc1Weights[s], m_Fc1Weights[s], v_Fc1Weights[s], lrCorrected, beta1, beta2, eps, Int8Max);
            AdamUpdate(Fc2Biases[s],  gradFc2Biases[s],  m_Fc2Biases[s],  v_Fc2Biases[s],  lrCorrected, beta1, beta2, eps, float.MaxValue);
            AdamUpdate(Fc2Weights[s], gradFc2Weights[s], m_Fc2Weights[s], v_Fc2Weights[s], lrCorrected, beta1, beta2, eps, Int8Max);
        }
    }

    private static void AdamUpdate(
        float[] param, float[] grad, float[] m, float[] v,
        float lr, float beta1, float beta2, float eps, float clipMax)
    {
        for (int i = 0; i < param.Length; i++)
        {
            m[i] = beta1 * m[i] + (1f - beta1) * grad[i];
            v[i] = beta2 * v[i] + (1f - beta2) * grad[i] * grad[i];
            float update = lr * m[i] / (MathF.Sqrt(v[i]) + eps);
            param[i] = Math.Clamp(param[i] - update, -clipMax, clipMax);
        }
    }

    // ── 私有輔助 ─────────────────────────────────────────────────────

    /// <summary>計算雙視角 FT 累加器並就地寫入 <paramref name="acc"/>（重用預分配陣列，無堆積配置）。</summary>
    private void ComputeFtAccumulator(IBoard board, float[,] acc)
    {
        for (int c = 0; c < 2; c++)
        {
            var perspective = c == 0 ? PieceColor.Red : PieceColor.Black;

            // FT biases（×FtBiasScale 還原縮放）
            for (int n = 0; n < L1; n++) acc[c, n] = FtBiases[n] * FtBiasScale;

            bool mm    = MidMirrorEncoder.RequiresMidMirror(board, perspective);
            int  count = HalfKAv2Features.GetActiveFeatures(board, perspective, mm, featuresWork);

            // 快取特徵索引供 ComputePsqt 和 Backward Step I 重用，避免重複呼叫 GetActiveFeatures
            if (c == 0) { cachedFeatCount0 = count; Array.Copy(featuresWork, cachedFeats0, count); }
            else        { cachedFeatCount1 = count; Array.Copy(featuresWork, cachedFeats1, count); }

            for (int f = 0; f < count; f++)
            {
                int featIdx = featuresWork[f];
                int wOffset = (int)((long)featIdx * L1);
                for (int n = 0; n < L1; n++)
                    acc[c, n] += FtWeights[wOffset + n];
            }
        }
    }

    private float ComputePsqt(int usPerspColorIdx, int stackBucket)
    {
        float us = 0f, them = 0f;

        for (int c = 0; c < 2; c++)
        {
            int   count = c == 0 ? cachedFeatCount0 : cachedFeatCount1;
            int[] feats = c == 0 ? cachedFeats0 : cachedFeats1;
            float sum   = 0f;
            for (int f = 0; f < count; f++)
            {
                int pOffset = (int)((long)feats[f] * PsqtBuckets + stackBucket);
                sum += FtPsqt[pOffset];
            }
            if (c == usPerspColorIdx) us = sum; else them = sum;
        }
        return (us - them) / 2f;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    // ── 測試輔助（internal，供 ChineseChess.Tests 使用）──────────────────

    /// <summary>
    /// 檢查所有梯度陣列均無 NaN 或 Infinity（供測試驗證）。
    /// ⚠️ 僅供測試使用：此方法需迭代 gradFtWeights（約 16.7M floats），
    ///    請勿在訓練迴圈中呼叫。
    /// </summary>
    internal bool AllGradientsFinite() => ValidateAllGradients(float.IsFinite);

    /// <summary>
    /// 檢查所有梯度陣列均為零（ZeroGradients 後驗證用）。
    /// ⚠️ 僅供測試使用：同 AllGradientsFinite，請勿在訓練迴圈中呼叫。
    /// </summary>
    internal bool AllGradientsZero() => ValidateAllGradients(v => v == 0f);

    private bool ValidateAllGradients(Func<float, bool> check)
    {
        if (!AllSatisfy(gradFtBiases, check) || !AllSatisfy(gradFtWeights, check) || !AllSatisfy(gradFtPsqt, check))
            return false;
        for (int s = 0; s < Stacks; s++)
        {
            if (!AllSatisfy(gradFc0Biases[s], check) || !AllSatisfy(gradFc0Weights[s], check)) return false;
            if (!AllSatisfy(gradFc1Biases[s], check) || !AllSatisfy(gradFc1Weights[s], check)) return false;
            if (!AllSatisfy(gradFc2Biases[s], check) || !AllSatisfy(gradFc2Weights[s], check)) return false;
        }
        return true;
    }

    private static bool AllSatisfy(float[] arr, Func<float, bool> check)
    {
        foreach (float v in arr)
            if (!check(v)) return false;
        return true;
    }

    // ── LayerStack 選桶（委派至 LayerStackBucketHelper，與推論版共用邏輯）

    private static float[][] AllocJagged(int outer, Func<int, int> innerLen)
    {
        var arr = new float[outer][];
        for (int i = 0; i < outer; i++) arr[i] = new float[innerLen(i)];
        return arr;
    }
}
