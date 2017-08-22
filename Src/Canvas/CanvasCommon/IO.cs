﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Illumina.Common;
using Illumina.Common.Collections;
using Illumina.Common.FileSystem;
using Isas.Framework.Logging;
using Isas.SequencingFiles;
using Isas.SequencingFiles.Vcf;

namespace CanvasCommon
{
    public class Genotype
    {
        public int CountsA { get; }
        public int CountsB { get; }

        public Genotype()
        {
            CountsA = 0;
            CountsB = 0;
        }

        public Genotype(int countsA, int countsB)
        {
            CountsA = countsA;
            CountsB = countsB;
        }
    }

    public class CanvasIO
    {
        public static void WriteToTextFile(string outfile, IEnumerable<SampleGenomicBin> bins)
        {
            using (GzipWriter writer = new GzipWriter(outfile))
            {
                foreach (SampleGenomicBin bin in bins)
                {
                    writer.WriteLine(string.Format("{0}\t{1}\t{2}\t{3:F2}\t{4}", bin.GenomicBin.Chromosome, bin.Start, bin.Stop, bin.Count, bin.GenomicBin.GC));
                }
            }
        }

        public static IEnumerable<SampleGenomicBin> IterateThroughTextFile(string infile)
        {
            using (GzipReader reader = new GzipReader(infile))
            {
                string row;

                while ((row = reader.ReadLine()) != null)
                {

                    string[] fields = row.Split('\t');

                    string chr = fields[0];
                    int start = Convert.ToInt32(fields[1]);
                    int stop = Convert.ToInt32(fields[2]);
                    float count = float.Parse(fields[3]);
                    int gc = Convert.ToInt32(fields[4]);

                    SampleGenomicBin bin = new SampleGenomicBin(chr, start, stop, gc, count);
                    yield return bin;
                }
            }
        }

        public static List<SampleGenomicBin> ReadFromTextFile(string infile)
        {
            return IterateThroughTextFile(infile).ToList();
        }

        /// <summary>
        /// Gets GenomicBins by chromosome as an OrderedDictionary. The order of chromosomes in path is preserved.
        /// Assumes that bins are sorted in ascending order by the start position within each chromosome.
        /// </summary>
        /// <param name="path"></param>
        /// <returns>OrderedDictionary with string key and List<GenomicBin> value</returns>
        public static OrderedDictionary<string, List<SampleGenomicBin>> GetGenomicBinsByChrom(string path)
        {
            OrderedDictionary<string, List<SampleGenomicBin>> binsByChrom = new OrderedDictionary<string, List<SampleGenomicBin>>();

            SampleGenomicBin prevBin = null;
            foreach (var bin in IterateThroughTextFile(path))
            {
                if (!binsByChrom.ContainsKey(bin.GenomicBin.Chromosome))
                {
                    binsByChrom[bin.GenomicBin.Chromosome] = new List<SampleGenomicBin>();
                    prevBin = null;
                }
                if (prevBin != null && bin.Start < prevBin.Start)
                    throw new Exception("Bins are not sorted in ascending order by the start position." +
                        $" First offending bin: {bin.GenomicBin.Chromosome}\t{bin.Start}\t{bin.Stop}");

                binsByChrom[bin.GenomicBin.Chromosome].Add(bin);
                prevBin = bin;
            }

            return binsByChrom;
        }

        public enum CoverageMetric
        {
            localSD,
            evenness,
        }
        // write localSD metric
        public static void WriteCoverageMetricToTextFile(string outfile, double coverageMetric, CoverageMetric coverageMetricType)
        {
            using (FileStream stream = new FileStream(outfile, FileMode.Append, FileAccess.Write))
            using (StreamWriter writer = new StreamWriter(stream))
            {
                writer.Write($"#{coverageMetricType.ToString()}\t" + coverageMetric);
                writer.WriteLine();
            }
        }

        // read localSD metric
        public static double ReadCoverageMetricFromTextFile(string infile, CoverageMetric coverageMetricType)
        {
            double coverageMetric = -1.0;
            using (FileStream stream = new FileStream(infile, FileMode.Open, FileAccess.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                string row;

                while ((row = reader.ReadLine()) != null)
                {
                    int tabs = (int)row.Count(ch => ch == '\t');
                    if (tabs > 0)
                    {
                        string[] fields = row.Split('\t');
                        string localSDstring = fields[0];
                        if (localSDstring == $"#{coverageMetricType}")
                            coverageMetric = Convert.ToDouble(fields[1]);
                    }
                }
            }

            return coverageMetric;
        }

        public static Dictionary<string, string> GetChromosomeAlternativeNames(IEnumerable<string> keys)
        {
            Dictionary<string, string> results = new Dictionary<string, string>();
            foreach (string key in keys)
            {
                if (key.StartsWith("chr"))
                {
                    results[key.Replace("chr", "")] = key;
                }
                else
                {
                    results["chr" + key] = key;
                }
            }
            return results;
        }

        public static HashSet<string> LoadChromosomeNames(string referenceFolder)
        {
            GenomeMetadata genomeMetaData = new GenomeMetadata();
            genomeMetaData.Deserialize(new FileLocation(Path.Combine(referenceFolder, "GenomeSize.xml")));
            var chromosomeNames = new HashSet<string>();
            foreach (var chromosome in genomeMetaData.Sequences)
                chromosomeNames.Add(chromosome.Name.ToLowerInvariant());
            return chromosomeNames;
        }

        public static Dictionary<string, List<List<Ballele>>> ReadFrequenciesWrapper(ILogger logger,
            IFileLocation variantFrequencyFile, Dictionary<string, List<BedInterval>> intervalsByChromosome)
        {
            using (var reader = new GzipOrTextReader(variantFrequencyFile.FullName))
            {
                logger.Info($"Load variant frequencies from {variantFrequencyFile}");
                return ReadFrequencies(logger, reader, intervalsByChromosome);
            }
        }


        public static Dictionary<string, List<List<Ballele>>> ReadFrequencies(ILogger logger, GzipOrTextReader variantFrequencyFileReader,
            Dictionary<string, List<BedInterval>> intervalByChromosome)
        {
            long totalRecords = 0;
            var alleleCountsByChromosome = new Dictionary<string, List<List<Ballele>>>();
            foreach (string chr in intervalByChromosome.Keys)
            {
                alleleCountsByChromosome[chr] = new List<List<Ballele>>();
                for (var index = 0; index < intervalByChromosome[chr].Count; index++)
                    alleleCountsByChromosome[chr].Add(new List<Ballele>());
            }

            while (true)
            {
                string fileLine = variantFrequencyFileReader.ReadLine();
                if (fileLine == null) break;
                if (fileLine.Length == 0 || fileLine[0] == '#') continue; // Skip headers
                var columns = fileLine.Split('\t');
                if (columns.Length < 6)
                {
                    logger.Info($"* Bad line {fileLine}'");
                    continue;
                }
                string chr = columns[0];
                int position = int.Parse(columns[1]); // 1-based (from the input VCF to Canvas SNV)
                int countRef = int.Parse(columns[4]);
                int countAlt = int.Parse(columns[5]);
                if (intervalByChromosome.Keys.All(chromosome => chromosome != chr))
                    continue;

                if (countRef + countAlt < 10) continue;
                // as both lists are sorted linear search should achieve an average O(log(n)) complexity
                int index = intervalByChromosome[chr].FindIndex(interval => interval.Start >= position && interval.End <= position);
                alleleCountsByChromosome[chr][index].Add(new Ballele(position, countRef, countAlt));
                totalRecords++;
                break;
            }
            logger.Info($"Loaded a total of {totalRecords} usable variant frequencies");
            return alleleCountsByChromosome;
        }

    }
}


