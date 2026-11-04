using System;
using System.Collections.Generic;

namespace PortableStreamer
{
    /// <summary>
    /// Собирает целые JPEG-кадры из произвольного потока байт.
    /// Хвост недособранного кадра сохраняется для следующего чтения,
    /// поэтому кадр, разрезанный между двумя пакетами TCP, не теряется.
    /// </summary>
    internal sealed class JpegFramer
    {
        private const int MaxBufferBytes = 4 * 1024 * 1024;

        private readonly List<byte> _buffer = new(256 * 1024);

        public List<byte[]> Push(ReadOnlySpan<byte> chunk)
        {
            var frames = new List<byte[]>();

            foreach (byte b in chunk)
                _buffer.Add(b);

            // Защита от мусора: если начала кадра нет на несколько мегабайт — сбрасываем.
            if (_buffer.Count > MaxBufferBytes)
                _buffer.Clear();

            while (true)
            {
                int start = IndexOfMarker(0, 0xD8);
                if (start < 0)
                {
                    // Начало кадра не найдено. Оставляем последний байт: он может быть
                    // первым байтом маркера 0xFF 0xD8, разрезанного между чтениями.
                    if (_buffer.Count > 1)
                        _buffer.RemoveRange(0, _buffer.Count - 1);
                    break;
                }

                int end = IndexOfMarker(start + 2, 0xD9);
                if (end < 0)
                {
                    // Кадр ещё не пришёл целиком — оставляем его начало в буфере.
                    if (start > 0)
                        _buffer.RemoveRange(0, start);
                    break;
                }

                frames.Add(_buffer.GetRange(start, end + 1 - start).ToArray());
                _buffer.RemoveRange(0, end + 1);
            }

            return frames;
        }

        private int IndexOfMarker(int fromIndex, byte secondByte)
        {
            int begin = Math.Max(0, fromIndex);
            for (int i = begin; i + 1 < _buffer.Count; i++)
            {
                if (_buffer[i] == 0xFF && _buffer[i + 1] == secondByte)
                    return i;
            }

            return -1;
        }
    }
}
