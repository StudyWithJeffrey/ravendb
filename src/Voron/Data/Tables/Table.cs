using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Sparrow;
using Voron.Data.BTrees;
using Voron.Data.Fixed;
using Voron.Data.RawData;
using Voron.Impl;
using Voron.Impl.Paging;
using Voron.Util.Conversion;

namespace Voron.Data.Tables
{
    public unsafe class Table
    {
        private Dictionary<Slice, Tree> _treesBySlice;
        private Dictionary<string, Slice> _sliceByName;

        private readonly TableSchema _schema;
        private readonly Transaction _tx;
        private readonly string _name;
        private readonly Tree _tableTree;

        private ActiveRawDataSmallSection _activeDataSmallSection;
        private FixedSizeTree _fstKey;
        private FixedSizeTree _inactiveSections;
        private FixedSizeTree _activeCandidateSection;
        private readonly int _pageSize;

        public FixedSizeTree FixedSizeKey
        {
            get
            {
                if (_fstKey == null)
                    _fstKey = new FixedSizeTree(_tx.LowLevelTransaction, _tableTree, _schema.Key.NameAsSlice, sizeof(long));
                return _fstKey;
            }
        }

        public FixedSizeTree InactiveSections
        {
            get
            {
                if (_inactiveSections == null)
                {
                    var inactiveSectionSlice = TableSchema.InactiveSectionSlice;
                    _inactiveSections = new FixedSizeTree(_tx.LowLevelTransaction,
                        _tableTree, inactiveSectionSlice, 0);
                }
                return _inactiveSections;
            }
        }


        public FixedSizeTree ActiveCandidateSection
        {
            get
            {
                if (_activeCandidateSection == null)
                {
                    var availableSectionSlice = TableSchema.ActiveCandidateSection;
                    _activeCandidateSection = new FixedSizeTree(_tx.LowLevelTransaction, _tableTree, availableSectionSlice, 0);
                }
                return _activeCandidateSection;
            }
        }

        public ActiveRawDataSmallSection ActiveDataSmallSection
        {
            get
            {
                if (_activeDataSmallSection == null)
                {
                    var readResult = _tableTree.Read(TableSchema.ActiveSectionSlice);
                    if (readResult == null)
                        throw new InvalidDataException($"Could not find active sections for {_name}");

                    long pageNumber = readResult.Reader.ReadLittleEndianInt64();

                    _activeDataSmallSection = new ActiveRawDataSmallSection(_tx.LowLevelTransaction, pageNumber);
                    _activeDataSmallSection.DataMoved += OnDataMoved;
                }
                return _activeDataSmallSection;
            }
        }

        private void OnDataMoved(long previousId, long newId, byte* data, int size)
        {
            DeleteValueFromIndex(previousId, new TableValueReader(data, size));
            InsertIndexValuesFor(newId, new TableValueReader(data, size));
        }

        public Table(TableSchema schema, string name, Transaction tx)
        {
            _schema = schema;
            _tx = tx;
            _name = name;
            _tableTree = _tx.ReadTree(name);
            _pageSize = _tx.LowLevelTransaction.DataPager.PageSize;

            var stats = (TableSchemaStats*)_tableTree.DirectRead(TableSchema.StatsSlice);
            if (stats == null)
                throw new InvalidDataException($"Cannot find stats value for table {name}");
            NumberOfEntries = stats->NumberOfEntries;
        }

        /// <summary>
        /// this overload is meant to be used for global reads only, when want to use
        /// a global index to find data, without touching the actual table.
        /// </summary>
        public Table(TableSchema schema, Transaction tx)
        {
            _schema = schema;
            _tx = tx;
            _pageSize = _tx.LowLevelTransaction.DataPager.PageSize;
        }


        public TableValueReader ReadByKey(Slice key)
        {
            long id;
            if (TryFindIdFromPrimaryKey(key, out id) == false)
                return null;

            int size;
            var rawData = DirectRead(id, out size);
            return new TableValueReader(rawData, size)
            {
                Id = id
            };
        }

        private bool TryFindIdFromPrimaryKey(Slice key, out long id)
        {
            var pkTree = GetTree(_schema.Key);
            var readResult = pkTree.Read(key);
            if (readResult == null)
            {
                id = -1;
                return false;
            }

            id = readResult.Reader.ReadLittleEndianInt64();
            return true;
        }

        private Tree GetTree(TableSchema.SchemaIndexDef idx)
        {
            if (idx.IsGlobal)
                return _tx.ReadTree(idx.Name);
            return GetTree2(idx.NameAsSlice);

        }

        private byte* DirectRead(long id, out int size)
        {
            var posInPage = id % _pageSize;
            if (posInPage == 0) // large
            {
                var page = _tx.LowLevelTransaction.GetPage(id / _pageSize);
                size = page.OverflowSize;

                return page.Pointer + sizeof(PageHeader);
            }

            // here we rely on the fact that RawDataSmallSection can 
            // read any RawDataSmallSection piece of data, not just something that
            // it exists in its own section, but anything from other sections as well
            return RawDataSection.DirectRead(_tx.LowLevelTransaction, id, out size);
        }

        public void Update(long id, TableValueBuilder builder)
        {
            int size = builder.Size;

            // first, try to fit in place, either in small or large sections
            var prevIsSmall = id % _pageSize != 0;
            if (size < ActiveDataSmallSection.MaxItemSize)
            {
                byte* pos;
                if (prevIsSmall && ActiveDataSmallSection.TryWriteDirect(id, size, out pos))
                {
                    int oldDataSize;
                    var oldData = ActiveDataSmallSection.DirectRead(id, out oldDataSize);

                    DeleteValueFromIndex(id, new TableValueReader(oldData, oldDataSize));

                    // MemoryCopy into final position.
                    builder.CopyTo(pos);
                    InsertIndexValuesFor(id, new TableValueReader(pos, size));
                    return;
                }
            }
            else if (prevIsSmall == false)
            {
                var pageNumber = id / _pageSize;
                var page = _tx.LowLevelTransaction.GetPage(pageNumber);
                var existingNumberOfPages = _tx.LowLevelTransaction.DataPager.GetNumberOfOverflowPages(page.OverflowSize);
                var newNumberOfPages = _tx.LowLevelTransaction.DataPager.GetNumberOfOverflowPages(size);

                if (existingNumberOfPages == newNumberOfPages)
                {
                    page.OverflowSize = size;
                    var pos = page.Pointer + sizeof(PageHeader);

                    DeleteValueFromIndex(id, new TableValueReader(pos, size));

                    // MemoryCopy into final position.
                    builder.CopyTo(pos);

                    InsertIndexValuesFor(id, new TableValueReader(pos, size));

                    return;
                }
            }

            // can't fit in place, will just delete & insert instead
            Delete(id);
            Insert(builder);
        }

        private void Delete(long id)
        {
            int size;
            var ptr = DirectRead(id, out size);
            if (ptr == null)
                return;

            DeleteValueFromIndex(id, new TableValueReader(ptr, size));

            var largeValue = (id % _pageSize) == 0;
            if (largeValue)
            {
                var page = _tx.LowLevelTransaction.GetPage(id / _pageSize);
                var numberOfPages = _tx.LowLevelTransaction.DataPager.GetNumberOfOverflowPages(page.OverflowSize);
                for (int i = 0; i < numberOfPages; i++)
                {
                    _tx.LowLevelTransaction.FreePage(page.PageNumber + i);
                }
                return;
            }

            var density = ActiveDataSmallSection.Free(id);
            if (ActiveDataSmallSection.Contains(id) || density > 0.5)
                return;

            var sectionPageNumber = ActiveDataSmallSection.GetSectionPageNumber(id);
            if (density > 0.15)
            {
                ActiveCandidateSection.Add(sectionPageNumber);
                return;
            }

            // move all the data to the current active section (maybe creating a new one 
            // if this is busy)

            // if this is in the active candidate list, remove it so it cannot be reused if the current
            // active is full and need a new one
            ActiveCandidateSection.Delete(sectionPageNumber);

            var idsInSection = ActiveDataSmallSection.GetAllIdsInSectionContaining(id);
            foreach (var idToMove in idsInSection)
            {
                int itemSize;
                var pos = ActiveDataSmallSection.DirectRead(idToMove, out itemSize);
                var newId = AllocateFromSmallActiveSection(size);

                OnDataMoved(idToMove, newId, pos, itemSize);

                byte* writePos;
                if (ActiveDataSmallSection.TryWriteDirect(newId, itemSize, out writePos) == false)
                    throw new InvalidDataException($"Cannot write to newly allocated size in {_name} during delete");

                Memory.Copy(writePos, pos, itemSize);
            }

            ActiveDataSmallSection.DeleteSection(sectionPageNumber);
        }


        private void DeleteValueFromIndex(long id, TableValueReader value)
        {
            //TODO: Avoid all those allocations by using a single buffer
            var keySlice = _schema.Key.GetSlice(value);
            var pkTree = GetTree(_schema.Key);
            pkTree.Delete(keySlice);

            foreach (var indexDef in _schema.Indexes.Values)
            {
                if (indexDef.CanUseFixedSizeTree)
                {
                    var tableTree = _tx.CreateTree(_name);
                    var fst = new FixedSizeTree(_tx.LowLevelTransaction, tableTree, indexDef.NameAsSlice, sizeof(long));
                    fst.Delete(id);
                }
                else
                {
                    var indexTree = GetTree(indexDef);
                    var val = indexDef.GetSlice(value);

                    var fst = new FixedSizeTree(_tx.LowLevelTransaction, indexTree, val, 0);
                    fst.Delete(id);
                }
            }
        }

        public void Insert(TableValueBuilder builder)
        {
            var stats = (TableSchemaStats*)_tableTree.DirectAdd(TableSchema.StatsSlice, sizeof(TableSchemaStats));
            NumberOfEntries++;
            stats->NumberOfEntries = NumberOfEntries;


            int size = builder.Size;

            byte* pos;
            long id;
            if (size < ActiveDataSmallSection.MaxItemSize)
            {
                id = AllocateFromSmallActiveSection(size);

                if (ActiveDataSmallSection.TryWriteDirect(id, size, out pos) == false)
                    throw new InvalidOperationException($"After successfully allocating {size:#,#;;0} bytes, failed to write them on {_name}");

                // MemoryCopy into final position.
                builder.CopyTo(pos);
            }
            else
            {
                var numberOfOverflowPages = _tx.LowLevelTransaction.DataPager.GetNumberOfOverflowPages(size);
                var page = _tx.LowLevelTransaction.AllocatePage(numberOfOverflowPages);
                page.Flags = PageFlags.Overflow | PageFlags.RawData;
                page.OverflowSize = size;

                pos = page.Pointer + sizeof(PageHeader);

                builder.CopyTo(pos);

                id = page.PageNumber;
            }

            InsertIndexValuesFor(id, new TableValueReader(pos, size));
        }

        private void InsertIndexValuesFor(long id, TableValueReader value)
        {
            var isAsBytes = EndianBitConverter.Little.GetBytes(id);

            var pkval = _schema.Key.GetSlice(value);
            var pkIndex = GetTree(_schema.Key);
            pkIndex.Add(pkval, isAsBytes, 0);

            foreach (var indexDef in _schema.Indexes.Values)
            {
                var val = indexDef.GetSlice(value);
                if (indexDef.CanUseFixedSizeTree)
                {
                    var tableTree = _tx.ReadTree(_name);
                    var index = new FixedSizeTree(_tx.LowLevelTransaction, tableTree, indexDef.NameAsSlice, sizeof(long));
                    Debug.Assert(val.Size == sizeof(long));
                    long key;
                    val.CopyTo((byte*) &key);
                    index.Add(key, new Slice((byte*) &id, sizeof (long)));
                }
                else
                {
                    var indexTree = GetTree(indexDef);
                    var index = new FixedSizeTree(_tx.LowLevelTransaction, indexTree, val, 0);
                    index.Add(id);
                }
            }
        }

        private long AllocateFromSmallActiveSection(int size)
        {
            long id;
            if (ActiveDataSmallSection.TryAllocate(size, out id) == false)
            {
                InactiveSections.Add(_activeDataSmallSection.PageNumber);

                using (var it = ActiveCandidateSection.Iterate())
                {
                    if (it.Seek(long.MinValue))
                    {
                        do
                        {
                            var sectionPageNumber = it.CurrentKey;
                            _activeDataSmallSection = new ActiveRawDataSmallSection(_tx.LowLevelTransaction,
                                sectionPageNumber);
                            if (_activeDataSmallSection.TryAllocate(size, out id))
                            {
                                ActiveCandidateSection.Delete(sectionPageNumber);
                                return id;
                            }
                        } while (it.MoveNext());

                    }
                }

                _activeDataSmallSection = ActiveRawDataSmallSection.Create(_tx.LowLevelTransaction);
                _tableTree.Add(TableSchema.ActiveSectionSlice, EndianBitConverter.Little.GetBytes(_activeDataSmallSection.PageNumber));

                var allocationResult = _activeDataSmallSection.TryAllocate(size, out id);

                Debug.Assert(allocationResult);
            }
            return id;
        }

        public long NumberOfEntries { get; private set; }

        private Tree GetTree(string name, out Slice slice)
        {
            if (_sliceByName == null)
                _sliceByName = new Dictionary<string, Slice>();
            if (_sliceByName.TryGetValue(name, out slice) == false)
            {
                slice = name;
                _sliceByName[name] = slice;
            }
            return GetTree2(slice);

        }

        private Tree GetTree2(Slice name)
        {
            if (_treesBySlice == null)
                _treesBySlice = new Dictionary<Slice, Tree>();

            Tree tree;
            if (_treesBySlice.TryGetValue(name, out tree))
                return tree;

            var treeHeader = _tableTree.DirectRead(name);
            if (treeHeader == null)
                throw new InvalidOperationException($"Cannot find tree {name} in table {_name}");

            tree = Tree.Open(_tx.LowLevelTransaction, _tx, (TreeRootHeader*)treeHeader);
            _treesBySlice[name] = tree;
            return tree;
        }

        public void DeleteByKey(Slice key)
        {
            var pkTree = GetTree(_schema.Key);

            var readResult = pkTree.Read(key);
            if (readResult == null)
                return;

            // This is an implementation detail. We read the absolute location pointer (absolute offset on the file)
            var id = readResult.Reader.ReadLittleEndianInt64();

            // And delete the element accordingly. 
            Delete(id);
        }

        private IEnumerable<TableValueReader> GetSecondaryIndexForValue(Tree tree, Slice value)
        {
            var fstIndex = new FixedSizeTree(_tx.LowLevelTransaction, tree, value, 0);
            using (var it = fstIndex.Iterate())
            {
                if (it.Seek(long.MinValue) == false)
                    yield break;

                do
                {
                    yield return ReadById(it.CurrentKey);
                } while (it.MoveNext());
            }
        }

        private TableValueReader ReadById(long id)
        {
            int size;
            var ptr = DirectRead(id, out size);
            var secondaryIndexForValue = new TableValueReader(ptr, size);
            return secondaryIndexForValue;
        }

        public class SeekResult
        {
            public Slice Key;
            public IEnumerable<TableValueReader> Results;
        }

        public IEnumerable<SeekResult> SeekTo(TableSchema.SchemaIndexDef index, Slice value)
        {
            var tree = GetTree(index);
            using (var it = tree.Iterate())
            {
                if (it.Seek(value) == false)
                    yield break;

                do
                {
                    yield return new SeekResult
                    {
                        Key = it.CurrentKey,
                        Results = GetSecondaryIndexForValue(tree, it.CurrentKey)
                    };

                }
                while (it.MoveNext());
            }
        }

        public void Set(TableValueBuilder builder)
        {
            int size;
            var read = builder.Read(_schema.Key.StartIndex, out size);
            long id;
            if (TryFindIdFromPrimaryKey(new Slice(read, (ushort)size), out id))
            {
                Update(id, builder);
                return;
            }
            Insert(builder);
        }
    }
}