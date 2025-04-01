# Frame Writer Prototype

This is a very barebones prototype for a FrameWriter to write DataFrame objects produced by Onix1

Its core functionality is a set of `Write` methods for different types that can appear on DataFrames
and the use of reflection to dynamically inspect the source frame types, extract its properties and direct them 
to the appropriate Writer method. Reflection mapping is done at startup so runtime invocation should be fast.

This *could* be a generic object writer. However, since we need to define specific writing methods for some
specific classes, it would be difficult to maintain, while 