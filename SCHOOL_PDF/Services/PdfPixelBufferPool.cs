namespace SchoolPdf.Services;

/// <summary>页面渲染输出像素缓冲复用池（借鉴 KillerPDF PdfEncodingBufferPool）：
/// 每次渲染都新建 byte[]（1080p 一页 ≈20MB）会持续产生大对象堆垃圾，改为顺序渲染间共享备用缓冲。
/// 页面渲染并发为 2（PdfRenderService._pageRenderSem）：只留一块时，两条线程同时渲染会
/// 「A 取走备用块→B 只能新建→B 归还时因已有备用块被丢弃」，滚动时仍反复分配 ≈20MB 大对象、
/// 拉开 GC 间隔把书写/翻页顶出一次长停顿。因此保留到 2 块（恰好覆盖并发数），
/// 单块上限 64MB，超出直接丢弃，避免池本身变成新占用。</summary>
internal static class PdfPixelBufferPool
{
    private const long MaximumRetainedBytes = 64L * 1024 * 1024;
    private const int MaximumRetainedBuffers = 2;

    private static readonly object Sync = new();
    private static readonly List<byte[]> Spares = new(MaximumRetainedBuffers);

    /// <summary>取一块至少 minimumLength 字节的缓冲：优先命中足够大的备用块（取走），否则新建。
    /// 用 <see cref="System.GC.AllocateUninitializedArray{T}(int)"/> 跳过清零——调用方（PDFium）
    /// 会先 FillRect 铺白底再绘制，旧内容不会被读到。</summary>
    internal static byte[] Rent(int minimumLength)
    {
        if (minimumLength <= 0)
            return Array.Empty<byte>();
        lock (Sync)
        {
            // 取“够用的里面最小的一块”（best-fit）：大块留给真正的大页，减少小块页白占大缓冲
            int best = -1;
            for (int i = 0; i < Spares.Count; i++)
            {
                if (Spares[i].Length < minimumLength)
                    continue;
                if (best < 0 || Spares[i].Length < Spares[best].Length)
                    best = i;
            }
            if (best >= 0)
            {
                var spare = Spares[best];
                Spares.RemoveAt(best);
                return spare;
            }
        }
        return System.GC.AllocateUninitializedArray<byte>(minimumLength);
    }

    /// <summary>归还缓冲：最多保留 2 块、单块上限 64MB；满员时用较大的一块替换最小的一块。</summary>
    internal static void Return(byte[] buffer)
    {
        if (buffer.Length == 0 || buffer.Length > MaximumRetainedBytes)
            return;
        lock (Sync)
        {
            for (int i = 0; i < Spares.Count; i++)
            {
                if (Spares[i].Length == buffer.Length)
                    return;   // 同尺寸已有一块，重复保留无意义
            }
            if (Spares.Count < MaximumRetainedBuffers)
            {
                Spares.Add(buffer);
                return;
            }
            int smallest = 0;
            for (int i = 1; i < Spares.Count; i++)
            {
                if (Spares[i].Length < Spares[smallest].Length)
                    smallest = i;
            }
            if (Spares[smallest].Length < buffer.Length)
                Spares[smallest] = buffer;
        }
    }
}
