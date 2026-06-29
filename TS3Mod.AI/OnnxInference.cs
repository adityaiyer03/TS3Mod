// Sample ONNX Runtime usage (requires Microsoft.ML.OnnxRuntime NuGet)
using System;
// using Microsoft.ML.OnnxRuntime;
// using Microsoft.ML.OnnxRuntime.Tensors;

namespace TS3Mod.AI
{
    public static class OnnxInference
    {
        // This is a simple usage example. Add the NuGet and adapt tensors to your model inputs/outputs.
        public static void Example(string modelPath)
        {
            // using var session = new InferenceSession(modelPath);
            // var inputMeta = session.InputMetadata;
            // create tensors and run
            Console.WriteLine("ONNX example placeholder: model path=" + modelPath);
        }
    }
}
