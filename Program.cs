using PortableStreamer;
using System;
using System.Threading;
using System.Windows.Forms;

namespace PortableStreamer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Один экземпляр на компьютер: два запуска конфликтуют за порты
            // 1935/8554, за файлы настроек и зря расходуют память.
            using var mutex = new Mutex(true, @"Global\PortableStreamer_PlayoutServer_SingleInstance", out bool isFirstInstance);

            if (!isFirstInstance)
            {
                MessageBox.Show(
                    "Программа уже запущена.\n\n" +
                    "Одновременно может работать только один экземпляр: " +
                    "он занимает порты 1935 и 8554, а второй запуск вызвал бы конфликт.\n\n" +
                    "Найдите открытое окно программы.",
                    "PortableStreamer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());

            GC.KeepAlive(mutex);
        }
    }
}
