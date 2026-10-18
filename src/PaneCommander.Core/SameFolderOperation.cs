namespace PaneCommander.Core;

public static class SameFolderOperation
{
    public static bool IsValidName(string original,string? name,FileOperationKind kind) =>
        !string.IsNullOrWhiteSpace(name) && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars())<0
        && !string.Equals(original,name,kind==FileOperationKind.Move?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase);
    public static FileOperationRequest Create(FileOperationKind kind,string source,string newName)
    {
        if(kind is not (FileOperationKind.Copy or FileOperationKind.Move))throw new ArgumentException("コピーまたは名前変更のみ対応します。");
        if(!IsValidName(Path.GetFileName(source),newName,kind))throw new ArgumentException("新しい名前が不正または元と同じです。");
        var request=new FileOperationRequest(kind,[source],Path.GetDirectoryName(source),newName);
        DropPolicy.Validate(request);return request;
    }
}
