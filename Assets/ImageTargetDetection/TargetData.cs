using System;
using System.Collections.Generic;

/// <summary>
/// Data models matching the API response from the SAFER AR HuggingFace Space.
/// </summary>

[Serializable]
public class TargetContent
{
    public string text;
    public List<string> image_urls;
    public string video_url;
    public string model_3d_url;
}

[Serializable]
public class TargetData
{
    public string id;
    public string name;
    public string description;
    public string target_image_url;
    public TargetContent content;
}

[Serializable]
public class TargetList
{
    public List<TargetData> targets;
}
