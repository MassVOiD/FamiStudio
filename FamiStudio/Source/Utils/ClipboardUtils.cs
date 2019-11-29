﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace FamiStudio
{
    public static class ClipboardUtils
    {
        const uint MagicNumberClipboardNotes    = 0x214E4346; // FCN!
        const uint MagicNumberClipboardEnvelope = 0x21454346; // FCE!
        const uint MagicNumberClipboardPatterns = 0x21504346; // FCP!

#if !FAMISTUDIO_WINDOWS
        static byte[] macClipboardData; // Cant copy between FamiStudio instance on MacOS.
#endif

        private static void SetClipboardData(byte[] data)
        {
#if FAMISTUDIO_WINDOWS
            Clipboard.SetData("FamiStudio", data);
#else
            macClipboardData = data;
#endif
        }

        private static byte[] GetClipboardData(uint magic)
        {
#if FAMISTUDIO_WINDOWS
            var buffer = Clipboard.GetData("FamiStudio") as byte[];
#else
            var buffer = macClipboardData;
#endif

            if (buffer == null || BitConverter.ToUInt32(buffer, 0) != magic)
                return null;

            return buffer;
        }

        public static bool ConstainsNotes    => GetClipboardData(MagicNumberClipboardNotes) != null;
        public static bool ConstainsEnvelope => GetClipboardData(MagicNumberClipboardEnvelope) != null;
        public static bool ConstainsPatterns => GetClipboardData(MagicNumberClipboardPatterns) != null;

        private static void SaveInstrumentList(ProjectSaveBuffer serializer, ICollection<Instrument> instruments)
        {
            int numInstruments = instruments.Count;
            serializer.Serialize(ref numInstruments);

            foreach (var inst in instruments)
            {
                var instId = inst.Id;
                var instName = inst.Name;
                serializer.Serialize(ref instId);
                serializer.Serialize(ref instName);
            }
        }

        private static bool LoadAndMergeInstrumentList(ProjectLoadBuffer serializer, bool checkOnly = false, bool createMissing = true)
        {
            int numInstruments = 0;
            serializer.Serialize(ref numInstruments);

            var instrumentIdNameMap = new List<Tuple<int, string>>();
            for (int i = 0; i < numInstruments; i++)
            {
                var instId = 0;
                var instName = "";
                serializer.Serialize(ref instId);
                serializer.Serialize(ref instName);
                instrumentIdNameMap.Add(new Tuple<int, string>(instId, instName));
            }

            var dummyInstrument = new Instrument();
            var needMerge = false;

            for (int i = 0; i < numInstruments; i++)
            {
                var instId = instrumentIdNameMap[i].Item1;
                var instName = instrumentIdNameMap[i].Item2;

                Instrument instrument = null;
                var existingInstrument = serializer.Project.GetInstrument(instName);

                if (existingInstrument != null)
                {
                    serializer.RemapId(instId, existingInstrument.Id);
                    instrument = existingInstrument;
                    dummyInstrument.SerializeState(serializer); // Skip instrument
                }
                else
                {
                    needMerge = true;

                    if (!checkOnly)
                    {
                        if (createMissing)
                        {
                            instrument = serializer.Project.CreateInstrument(instName);
                            serializer.RemapId(instId, instrument.Id);
                            instrument.SerializeState(serializer);
                        }
                        else
                        {
                            serializer.RemapId(instId, -1);
                            dummyInstrument.SerializeState(serializer); // Skip instrument
                        }
                    }
                }
            }

            return needMerge;
        }

        public static void SaveNotes(Project project, Note[] notes)
        {
            if (notes == null)
            {
                SetClipboardData(null);
                return;
            }

            var instruments = new HashSet<Instrument>();
            foreach (var note in notes)
            {
                if (note.Instrument != null)
                    instruments.Add(note.Instrument);
            }

            var serializer = new ProjectSaveBuffer(null);

            SaveInstrumentList(serializer, instruments);
            foreach (var inst in instruments)
                inst.SerializeState(serializer);

            int numNotes = notes.Length;
            serializer.Serialize(ref numNotes);
            foreach (var note in notes)
                note.SerializeState(serializer);
            
            var buffer = Compression.CompressBytes(serializer.GetBuffer(), CompressionLevel.Fastest);

            var clipboardData = new List<byte>();
            clipboardData.AddRange(BitConverter.GetBytes(MagicNumberClipboardNotes));
            clipboardData.AddRange(buffer);

            SetClipboardData(clipboardData.ToArray());
        }

        public static bool ContainsMissingInstruments(Project project)
        {
            var buffer = GetClipboardData(MagicNumberClipboardNotes);

            if (buffer == null)
                return false;

            var serializer = new ProjectLoadBuffer(project, Compression.DecompressBytes(buffer, 4), Project.Version);

            return LoadAndMergeInstrumentList(serializer, true);
        }

        public static Note[] LoadNotes(Project project, bool createMissingInstruments)
        {
            var buffer = GetClipboardData(MagicNumberClipboardNotes);

            if (buffer == null)
                return null;

            var serializer = new ProjectLoadBuffer(project, Compression.DecompressBytes(buffer, 4), Project.Version);

            LoadAndMergeInstrumentList(serializer, false, createMissingInstruments);

            int numNotes = 0;
            serializer.Serialize(ref numNotes);
            var notes = new Note[numNotes];
            for (int i = 0; i < numNotes; i++)
                notes[i].SerializeState(serializer);

            project.SortInstruments();

            return notes;
        }

        public static void SaveEnvelopeValues(sbyte[] values)
        {
            var clipboardData = new List<byte>();
            clipboardData.AddRange(BitConverter.GetBytes(MagicNumberClipboardEnvelope));
            clipboardData.AddRange(BitConverter.GetBytes(values.Length));
            for (int i = 0; i < values.Length; i++)
                clipboardData.Add((byte)values[i]);

            SetClipboardData(clipboardData.ToArray());
        }

        public static sbyte[] LoadEnvelopeValues()
        {
            var buffer = GetClipboardData(MagicNumberClipboardEnvelope);

            if (buffer == null)
                return null;

            var numValues = BitConverter.ToInt32(buffer, 4);
            var values = new sbyte[numValues];

            for (int i = 0; i < numValues; i++)
                values[i] = (sbyte)buffer[8 + i];

            return values;
        }

        public static void SavePatterns(Project project, Pattern[,] patterns)
        {
            if (patterns == null)
            {
                SetClipboardData(null);
                return;
            }

            int numNonNullPatterns = 0;

            for (int i = 0; i < patterns.GetLength(0); i++)
            {
                for (int j = 0; j < patterns.GetLength(1); j++)
                {
                    if (patterns[i, j] != null)
                        numNonNullPatterns++;
                }
            }

            var serializer = new ProjectSaveBuffer(null);

            for (int i = 0; i < patterns.GetLength(0); i++)
            {
                for (int j = 0; j < patterns.GetLength(1); j++)
                {
                    var pattern = patterns[i, j];
                    if (pattern != null)
                    {
                        serializer.Serialize(ref i);
                        serializer.Serialize(ref j);
                        patterns[i, j].SerializeState(serializer);
                    }
                }
            }

            var buffer = Compression.CompressBytes(serializer.GetBuffer(), CompressionLevel.Fastest);

            var clipboardData = new List<byte>();
            clipboardData.AddRange(BitConverter.GetBytes(MagicNumberClipboardPatterns));
            clipboardData.AddRange(BitConverter.GetBytes(numNonNullPatterns));
            clipboardData.AddRange(BitConverter.GetBytes(patterns.GetLength(0)));
            clipboardData.AddRange(BitConverter.GetBytes(patterns.GetLength(1)));
            clipboardData.AddRange(buffer);

            SetClipboardData(clipboardData.ToArray());
        }

        public static Pattern[,] LoadPatterns(Project project)
        {
            var buffer = GetClipboardData(MagicNumberClipboardPatterns);

            if (buffer == null)
                return null;

            var numNonNullPatterns = BitConverter.ToInt32(buffer, 4);
            var numPatterns = BitConverter.ToInt32(buffer, 8);
            var numChannels = BitConverter.ToInt32(buffer, 12);
            var decompressedBuffer = Compression.DecompressBytes(buffer, 16);
            var serializer = new ProjectLoadBuffer(project, decompressedBuffer, Project.Version);

            var patterns = new Pattern[numPatterns, numChannels];

            for (int p = 0; p < numNonNullPatterns; p++)
            {
                int i = 0;
                int j = 0;
                serializer.Serialize(ref i);
                serializer.Serialize(ref j);
                patterns[i, j] = new Pattern();
                patterns[i, j].SerializeState(serializer);
            }

            return patterns;
        }

        public static void Reset()
        {
            SetClipboardData(null);
        }
    }
}
