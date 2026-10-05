using System;

namespace MphRead.Formats.Vx
{
    // The variable-length code tables of the residual (spec Appendix A, which restates H.264 Tables 9-5, 9-7, 9-8 and
    // 9-10). Codes are written first bit first. The data below was generated from the spec's tables.
    internal static class VxCodeTables
    {
        // coeff_token: one row per (TotalCoeff, TrailingOnes) pair; the symbol is TotalCoeff * 4 + TrailingOnes
        private static readonly (int Total, int Ones, string Nc0, string Nc2, string Nc4, string Nc8)[] _coeffToken =
        {
            (0, 0, "1", "11", "1111", "000011"),
            (1, 0, "000101", "001011", "001111", "000000"),
            (1, 1, "01", "10", "1110", "000001"),
            (2, 0, "00000111", "000111", "001011", "000100"),
            (2, 1, "000100", "00111", "01111", "000101"),
            (2, 2, "001", "011", "1101", "000110"),
            (3, 0, "000000111", "0000111", "001000", "001000"),
            (3, 1, "00000110", "001010", "01100", "001001"),
            (3, 2, "0000101", "001001", "01110", "001010"),
            (3, 3, "00011", "0101", "1100", "001011"),
            (4, 0, "0000000111", "00000111", "0001111", "001100"),
            (4, 1, "000000110", "000110", "01010", "001101"),
            (4, 2, "00000101", "000101", "01011", "001110"),
            (4, 3, "000011", "0100", "1011", "001111"),
            (5, 0, "00000000111", "00000100", "0001011", "010000"),
            (5, 1, "0000000110", "0000110", "01000", "010001"),
            (5, 2, "000000101", "0000101", "01001", "010010"),
            (5, 3, "0000100", "00110", "1010", "010011"),
            (6, 0, "0000000001111", "000000111", "0001001", "010100"),
            (6, 1, "00000000110", "00000110", "001110", "010101"),
            (6, 2, "0000000101", "00000101", "001101", "010110"),
            (6, 3, "00000100", "001000", "1001", "010111"),
            (7, 0, "0000000001011", "00000001111", "0001000", "011000"),
            (7, 1, "0000000001110", "000000110", "001010", "011001"),
            (7, 2, "00000000101", "000000101", "001001", "011010"),
            (7, 3, "000000100", "000100", "1000", "011011"),
            (8, 0, "0000000001000", "00000001011", "00001111", "011100"),
            (8, 1, "0000000001010", "00000001110", "0001110", "011101"),
            (8, 2, "0000000001101", "00000001101", "0001101", "011110"),
            (8, 3, "0000000100", "0000100", "01101", "011111"),
            (9, 0, "00000000001111", "000000001111", "00001011", "100000"),
            (9, 1, "00000000001110", "00000001010", "00001110", "100001"),
            (9, 2, "0000000001001", "00000001001", "0001010", "100010"),
            (9, 3, "00000000100", "000000100", "001100", "100011"),
            (10, 0, "00000000001011", "000000001011", "000001111", "100100"),
            (10, 1, "00000000001010", "000000001110", "00001010", "100101"),
            (10, 2, "00000000001101", "000000001101", "00001101", "100110"),
            (10, 3, "0000000001100", "00000001100", "0001100", "100111"),
            (11, 0, "000000000001111", "000000001000", "000001011", "101000"),
            (11, 1, "000000000001110", "000000001010", "000001110", "101001"),
            (11, 2, "00000000001001", "000000001001", "00001001", "101010"),
            (11, 3, "00000000001100", "00000001000", "00001100", "101011"),
            (12, 0, "000000000001011", "0000000001111", "000001000", "101100"),
            (12, 1, "000000000001010", "0000000001110", "000001010", "101101"),
            (12, 2, "000000000001101", "0000000001101", "000001101", "101110"),
            (12, 3, "00000000001000", "000000001100", "00001000", "101111"),
            (13, 0, "0000000000001111", "0000000001011", "0000001101", "110000"),
            (13, 1, "000000000000001", "0000000001010", "000000111", "110001"),
            (13, 2, "000000000001001", "0000000001001", "000001001", "110010"),
            (13, 3, "000000000001100", "0000000001100", "000001100", "110011"),
            (14, 0, "0000000000001011", "0000000000111", "0000001001", "110100"),
            (14, 1, "0000000000001110", "00000000001011", "0000001100", "110101"),
            (14, 2, "0000000000001101", "0000000000110", "0000001011", "110110"),
            (14, 3, "000000000001000", "0000000001000", "0000001010", "110111"),
            (15, 0, "0000000000000111", "00000000001001", "0000000101", "111000"),
            (15, 1, "0000000000001010", "00000000001000", "0000001000", "111001"),
            (15, 2, "0000000000001001", "00000000001010", "0000000111", "111010"),
            (15, 3, "0000000000001100", "0000000000001", "0000000110", "111011"),
            (16, 0, "0000000000000100", "00000000000111", "0000000001", "111100"),
            (16, 1, "0000000000000110", "00000000000110", "0000000100", "111101"),
            (16, 2, "0000000000000101", "00000000000101", "0000000011", "111110"),
            (16, 3, "0000000000001000", "00000000000100", "0000000010", "111111"),
        };

        // total_zeros for TotalCoeff 1..15; entry n of a row is the code for the value n
        private static readonly string[][] _totalZeros =
        {
            new[] { "1", "011", "010", "0011", "0010", "00011", "00010", "000011", "000010", "0000011", "0000010", "00000011", "00000010", "000000011", "000000010", "000000001" }, // TotalCoeff 1
            new[] { "111", "110", "101", "100", "011", "0101", "0100", "0011", "0010", "00011", "00010", "000011", "000010", "000001", "000000" }, // TotalCoeff 2
            new[] { "0101", "111", "110", "101", "0100", "0011", "100", "011", "0010", "00011", "00010", "000001", "00001", "000000" }, // TotalCoeff 3
            new[] { "00011", "111", "0101", "0100", "110", "101", "100", "0011", "011", "0010", "00010", "00001", "00000" }, // TotalCoeff 4
            new[] { "0101", "0100", "0011", "111", "110", "101", "100", "011", "0010", "00001", "0001", "00000" }, // TotalCoeff 5
            new[] { "000001", "00001", "111", "110", "101", "100", "011", "010", "0001", "001", "000000" }, // TotalCoeff 6
            new[] { "000001", "00001", "101", "100", "011", "11", "010", "0001", "001", "000000" }, // TotalCoeff 7
            new[] { "000001", "0001", "00001", "011", "11", "10", "010", "001", "000000" }, // TotalCoeff 8
            new[] { "000001", "000000", "0001", "11", "10", "001", "01", "00001" }, // TotalCoeff 9
            new[] { "00001", "00000", "001", "11", "10", "01", "0001" }, // TotalCoeff 10
            new[] { "0000", "0001", "001", "010", "1", "011" }, // TotalCoeff 11
            new[] { "0000", "0001", "01", "1", "001" }, // TotalCoeff 12
            new[] { "000", "001", "1", "01" }, // TotalCoeff 13
            new[] { "00", "01", "1" }, // TotalCoeff 14
            new[] { "0", "1" }, // TotalCoeff 15
        };

        // run_before for zerosLeft 1..6 and "more than 6"; entry n of a row is the code for the value n
        private static readonly string[][] _runBefore =
        {
            new[] { "1", "0" }, // zerosLeft 1
            new[] { "1", "01", "00" }, // zerosLeft 2
            new[] { "11", "10", "01", "00" }, // zerosLeft 3
            new[] { "11", "10", "01", "001", "000" }, // zerosLeft 4
            new[] { "11", "10", "011", "010", "001", "000" }, // zerosLeft 5
            new[] { "11", "000", "001", "011", "010", "101", "100" }, // zerosLeft 6
            new[] { "111", "110", "101", "100", "011", "010", "001", "0001", "00001", "000001", "0000001", "00000001", "000000001", "0000000001", "00000000001" }, // zerosLeft > 6
        };

        // indexed by nC range: 0 (nC 0-1), 1 (2-3), 2 (4-7), 3 (8 or more)
        public static readonly VxCodeTable[] CoeffToken = BuildCoeffToken();
        // indexed by TotalCoeff (entry 0 unused)
        public static readonly VxCodeTable[] TotalZeros = BuildList(_totalZeros, 1);
        // indexed by zerosLeft, 7 meaning "7 or more" (entry 0 unused)
        public static readonly VxCodeTable[] RunBefore = BuildList(_runBefore, 1);

        private static VxCodeTable[] BuildCoeffToken()
        {
            var tables = new VxCodeTable[4];
            for (int column = 0; column < 4; column++)
            {
                var codes = new string[_coeffToken.Length];
                var symbols = new int[_coeffToken.Length];
                for (int i = 0; i < _coeffToken.Length; i++)
                {
                    (int total, int ones, string nc0, string nc2, string nc4, string nc8) = _coeffToken[i];
                    codes[i] = column == 0 ? nc0 : column == 1 ? nc2 : column == 2 ? nc4 : nc8;
                    symbols[i] = total * 4 + ones;
                }
                tables[column] = new VxCodeTable(codes, symbols, rootBits: 8);
            }
            return tables;
        }

        private static VxCodeTable[] BuildList(string[][] rows, int firstIndex)
        {
            var tables = new VxCodeTable[rows.Length + firstIndex];
            for (int r = 0; r < rows.Length; r++)
            {
                string[] codes = rows[r];
                var symbols = new int[codes.Length];
                for (int i = 0; i < codes.Length; i++)
                {
                    symbols[i] = i;
                }
                tables[r + firstIndex] = new VxCodeTable(codes, symbols, rootBits: 8);
            }
            // entry 0 is never used; fill it so the array has no nulls
            for (int i = 0; i < firstIndex; i++)
            {
                tables[i] = tables[firstIndex];
            }
            return tables;
        }
    }

    // A prefix-free code as a lookup table: the first RootBits bits index the root level; codes longer than that
    // continue in a second-level table chosen by the root entry.
    //   Lengths[i] > 0: a code ends here; that many bits (of this level) are consumed and Symbols[i] is the value
    //   Lengths[i] < 0: consume this level's bits, then index the table at Symbols[i] with -Lengths[i] more bits
    //   Lengths[i] = 0: no code starts with these bits
    internal sealed class VxCodeTable
    {
        public readonly int RootBits;
        public readonly sbyte[] Lengths;
        public readonly int[] Symbols;

        public VxCodeTable(string[] codes, int[] symbols, int rootBits)
        {
            int maxLength = 0;
            foreach (string code in codes)
            {
                maxLength = Math.Max(maxLength, code.Length);
            }
            rootBits = Math.Min(rootBits, maxLength);
            RootBits = rootBits;
            int rootSize = 1 << rootBits;
            // how many more bits each root entry's second-level table needs
            var subBits = new int[rootSize];
            foreach (string code in codes)
            {
                if (code.Length > rootBits)
                {
                    int prefix = Value(code, rootBits);
                    subBits[prefix] = Math.Max(subBits[prefix], code.Length - rootBits);
                }
            }
            int size = rootSize;
            var subStart = new int[rootSize];
            for (int i = 0; i < rootSize; i++)
            {
                if (subBits[i] > 0)
                {
                    subStart[i] = size;
                    size += 1 << subBits[i];
                }
            }
            Lengths = new sbyte[size];
            Symbols = new int[size];
            for (int i = 0; i < rootSize; i++)
            {
                if (subBits[i] > 0)
                {
                    Lengths[i] = (sbyte)-subBits[i];
                    Symbols[i] = subStart[i];
                }
            }
            for (int c = 0; c < codes.Length; c++)
            {
                string code = codes[c];
                int start;
                int count;
                int length;
                if (code.Length <= rootBits)
                {
                    start = Value(code, code.Length) << (rootBits - code.Length);
                    count = 1 << (rootBits - code.Length);
                    length = code.Length;
                }
                else
                {
                    int prefix = Value(code, rootBits);
                    int extra = code.Length - rootBits;
                    int rest = Value(code.Substring(rootBits), extra);
                    start = subStart[prefix] + (rest << (subBits[prefix] - extra));
                    count = 1 << (subBits[prefix] - extra);
                    length = extra;
                }
                for (int i = start; i < start + count; i++)
                {
                    if (Lengths[i] != 0)
                    {
                        throw new ProgramException("VX code table is not prefix-free.");
                    }
                    Lengths[i] = (sbyte)length;
                    Symbols[i] = symbols[c];
                }
            }
        }

        private static int Value(string code, int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                value = (value << 1) | (code[i] == '1' ? 1 : 0);
            }
            return value;
        }
    }
}
