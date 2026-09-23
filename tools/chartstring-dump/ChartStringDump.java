import bms.model.BMSModel;
import bms.model.ChartDecoder;
import bms.model.ChartInformation;
import bms.player.beatoraja.play.BMSPlayerRule;

import java.io.BufferedReader;
import java.io.IOException;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;

public final class ChartStringDump {
    private static final Charset BMS_CHARSET = Charset.forName("MS932");

    public static void main(String[] args) throws Exception {
        Options options = Options.parse(args);
        Files.createDirectories(options.outputDir);
        for (Path chartPath : options.paths) {
            dumpOne(chartPath.toAbsolutePath().normalize(), options);
        }
    }

    private static void dumpOne(Path chartPath, Options options) {
        try {
            ChartDecoder decoder = ChartDecoder.getDecoder(chartPath);
            if (decoder == null) {
                writeJsonLine(options, jsonError(chartPath, "unsupported_extension", "No decoder for file extension."));
                return;
            }

            int[] selectedRandoms = isBmson(chartPath) ? null : countRandomsAsOnes(chartPath);
            BMSModel model = decoder.decode(new ChartInformation(chartPath, BMSModel.LNTYPE_LONGNOTE, selectedRandoms));
            if (model == null) {
                writeJsonLine(options, jsonError(chartPath, "decode_failed", "Decoder returned null."));
                return;
            }
            BMSPlayerRule.validate(model);

            String chartString = model.toChartString();
            String sha256 = emptyIfNull(model.getSHA256());
            String charthash = sha256Hex(chartString.getBytes());
            Path chartStringFile = null;
            if (options.chartStringDir != null) {
                Files.createDirectories(options.chartStringDir);
                String name = !sha256.isEmpty() ? sha256 : sha256Hex(Files.readAllBytes(chartPath));
                chartStringFile = options.chartStringDir.resolve(name + ".chart.txt");
                Files.write(chartStringFile, chartString.getBytes(StandardCharsets.UTF_8));
            }

            StringBuilder json = new StringBuilder();
            json.append("{\"ok\":true");
            appendJson(json, "path", chartPath.toString());
            appendJson(json, "sha256", sha256);
            appendJson(json, "charthash", charthash);
            appendJson(json, "selectedRandoms", selectedRandoms == null ? "" : join(selectedRandoms));
            if (chartStringFile != null) {
                appendJson(json, "chartStringPath", chartStringFile.toString());
            } else if (options.includeChartStringBase64) {
                appendJson(json, "chartStringBase64", Base64.getEncoder().encodeToString(chartString.getBytes(StandardCharsets.UTF_8)));
            }
            json.append("}");
            writeJsonLine(options, json.toString());
        } catch (Exception ex) {
            writeJsonLine(options, jsonError(chartPath, ex.getClass().getName(), ex.getMessage()));
        }
    }

    private static boolean isBmson(Path path) {
        String name = path.getFileName().toString().toLowerCase();
        return name.endsWith(".bmson");
    }

    private static int[] countRandomsAsOnes(Path chartPath) throws IOException {
        List<Integer> randoms = new ArrayList<>();
        try (BufferedReader reader = Files.newBufferedReader(chartPath, BMS_CHARSET)) {
            String line;
            while ((line = reader.readLine()) != null) {
                String trimmed = line.trim();
                if (trimmed.length() >= 8 && trimmed.regionMatches(true, 0, "#RANDOM", 0, 7)) {
                    randoms.add(1);
                }
            }
        }
        int[] result = new int[randoms.size()];
        for (int i = 0; i < result.length; i++) {
            result[i] = 1;
        }
        return result;
    }

    private static String join(int[] values) {
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < values.length; i++) {
            if (i > 0) {
                result.append(',');
            }
            result.append(values[i]);
        }
        return result.toString();
    }

    private static String sha256Hex(byte[] bytes) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] hashed = digest.digest(bytes);
        StringBuilder result = new StringBuilder(hashed.length * 2);
        for (byte b : hashed) {
            result.append(String.format("%02x", b & 0xff));
        }
        return result.toString();
    }

    private static String emptyIfNull(String value) {
        return value == null ? "" : value;
    }

    private static String jsonError(Path chartPath, String code, String message) {
        StringBuilder json = new StringBuilder();
        json.append("{\"ok\":false");
        appendJson(json, "path", chartPath.toString());
        appendJson(json, "error", code);
        appendJson(json, "message", message == null ? "" : message);
        json.append("}");
        return json.toString();
    }

    private static void appendJson(StringBuilder json, String name, String value) {
        json.append(",\"").append(escapeJson(name)).append("\":\"").append(escapeJson(value)).append("\"");
    }

    private static String escapeJson(String value) {
        StringBuilder escaped = new StringBuilder();
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '\\':
                    escaped.append("\\\\");
                    break;
                case '"':
                    escaped.append("\\\"");
                    break;
                case '\b':
                    escaped.append("\\b");
                    break;
                case '\f':
                    escaped.append("\\f");
                    break;
                case '\n':
                    escaped.append("\\n");
                    break;
                case '\r':
                    escaped.append("\\r");
                    break;
                case '\t':
                    escaped.append("\\t");
                    break;
                default:
                    if (c < 0x20) {
                        escaped.append(String.format("\\u%04x", (int)c));
                    } else {
                        escaped.append(c);
                    }
                    break;
            }
        }
        return escaped.toString();
    }

    private static void writeJsonLine(Options options, String json) {
        System.out.println(json);
    }

    private static final class Options {
        private final Path outputDir;
        private final Path chartStringDir;
        private final boolean includeChartStringBase64;
        private final List<Path> paths;

        private Options(Path outputDir, Path chartStringDir, boolean includeChartStringBase64, List<Path> paths) {
            this.outputDir = outputDir;
            this.chartStringDir = chartStringDir;
            this.includeChartStringBase64 = includeChartStringBase64;
            this.paths = paths;
        }

        private static Options parse(String[] args) throws IOException {
            Path outputDir = Path.of("artifacts", "chartstring-dump");
            Path chartStringDir = null;
            boolean includeChartStringBase64 = false;
            List<Path> paths = new ArrayList<>();
            for (int i = 0; i < args.length; i++) {
                String arg = args[i];
                if ("--output-dir".equals(arg)) {
                    outputDir = Path.of(requireValue(args, ++i, arg));
                } else if ("--chart-string-dir".equals(arg)) {
                    chartStringDir = Path.of(requireValue(args, ++i, arg));
                } else if ("--include-chart-string-base64".equals(arg)) {
                    includeChartStringBase64 = true;
                } else if ("--list".equals(arg)) {
                    Path list = Path.of(requireValue(args, ++i, arg));
                    for (String line : Files.readAllLines(list, StandardCharsets.UTF_8)) {
                        if (!line.trim().isEmpty()) {
                            paths.add(Path.of(line.trim()));
                        }
                    }
                } else {
                    paths.add(Path.of(arg));
                }
            }
            if (paths.isEmpty()) {
                throw new IllegalArgumentException("No chart paths specified. Use --list <file> or pass chart paths.");
            }
            return new Options(outputDir, chartStringDir, includeChartStringBase64, paths);
        }

        private static String requireValue(String[] args, int index, String option) {
            if (index >= args.length) {
                throw new IllegalArgumentException(option + " requires a value.");
            }
            return args[index];
        }
    }
}
